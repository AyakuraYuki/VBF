﻿// Copyright 2012 Fan Shi
// 
// This file is part of the VBF project.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VBF.Compilers.Scanners;

namespace VBF.Compilers.Parsers.Generator
{
    public class LR0Model
    {
        private ClosureVisitor m_closureVisitor = new ClosureVisitor();
        private DotSymbolVisitor m_dotSymbolVisitor = new DotSymbolVisitor();
        private ProductionInfoManager m_infoManager;
        private List<LR0State> m_states;

        public LR0Model(ProductionInfoManager infoManager)
        {
            CodeContract.RequiresArgumentNotNull(infoManager, "infoManager");

            m_infoManager = infoManager;
            m_states = new List<LR0State>();
        }

        public IReadOnlyList<LR0State> States
        {
            get
            {
                return m_states;
            }
        }

        public ProductionInfoManager ProductionInfoManager
        {
            get
            {
                return m_infoManager;
            }
        }

        public void BuildModel()
        {
            ISet<LR0Item> initStateSet = new HashSet<LR0Item>();
            initStateSet.Add(new LR0Item(m_infoManager.GetInfo(m_infoManager.RootProduction).Index, 0));

            initStateSet = GetClosure(initStateSet);

            LR0State initState = new LR0State(initStateSet);
            initState.Index = 0;

            m_states.Add(initState);


            bool isChanged = false;
            List<LR0State> changedStates = new List<LR0State>(m_states);
            List<LR0State> swapList = new List<LR0State>();

            //build shifts and gotos
            do
            {
                isChanged = false;

                //swap lists in different usages
                var states = changedStates;
                changedStates = swapList;
                changedStates.Clear();
                swapList = states;

                Parallel.ForEach(states,
                    /* 
                     * calculate goto/shift of each item set in parallel
                     */
                    state =>
                    {
                        state.PossibleEdges.Clear();

                        foreach (var item in state.ItemSet)
                        {
                            var production = m_infoManager.Productions[item.ProductionIndex];
                            var info = m_infoManager.GetInfo(production);

                            //m_dotSymbolVisitor.DotLocation = item.DotLocation;
                            var symbols = production.Accept(m_dotSymbolVisitor, item.DotLocation);
                            foreach (var symbol in symbols)
                            {
                                if (symbol.IsEos)
                                {
                                    //accept
                                    state.IsAcceptState = true;
                                    continue;
                                }

                                var targetStateSet = GetGoto(state.ItemSet, symbol);

                                state.PossibleEdges.Add(new KeyValuePair<IProduction, ISet<LR0Item>>(symbol, targetStateSet));

                                //calculate lexer states that the current LR0 state should cover
                                if (symbol.IsTerminal && !symbol.IsEos)
                                {
                                    Terminal t = symbol as Terminal;
                                    Token token = t.Token;

                                    state.AddShiftingLexer(token.LexerIndex);
                                }
                            }
                        }
                    }
                );

                foreach (var state in states)
                {

                    foreach (var possibleEdge in state.PossibleEdges)
                    {
                        var symbol = possibleEdge.Key;
                        var targetStateSet = possibleEdge.Value;

                        LR0State targetState;
                        //check if the target state is exist
                        bool exist = CheckExist(targetStateSet, out targetState);

                        if (exist)
                        {
                            //check edges
                            isChanged = state.AddEdge(symbol, targetState) || isChanged;
                        }
                        else
                        {
                            isChanged = true;

                            //create new state
                            targetState = new LR0State(targetStateSet);

                            targetState.Index = m_states.Count;
                            m_states.Add(targetState);

                            //create edge for it
                            state.AddEdge(symbol, targetState);

                            changedStates.Add(targetState);
                        }
                    }
                }

            } while (isChanged);


            //build reduces
            Parallel.ForEach(m_states,
                state =>
                {
                    foreach (var item in state.ItemSet)
                    {
                        var production = m_infoManager.Productions[item.ProductionIndex];
                        var info = m_infoManager.GetInfo(production);

                        if (item.DotLocation == info.SymbolCount)
                        {
                            foreach (var followSymbol in info.Follow)
                            {
                                //reduce
                                state.AddReduce(followSymbol, production);

                                if (!followSymbol.IsEos)
                                {
                                    Terminal t = followSymbol as Terminal;
                                    Token token = t.Token;

                                    state.AddReducingLexer(token.LexerIndex);
                                }
                            }

                        }
                    }
                }
            );
        }

        private bool CheckExist(ISet<LR0Item> set, out LR0State targetState)
        {
            bool exist = false;
            targetState = null;

            foreach (var state in m_states)
            {
                if (set.SetEquals(state.ItemSet))
                {
                    exist = true;
                    targetState = state;
                    break;
                }
            }

            return exist;
        }

        private ISet<LR0Item> GetClosure(ISet<LR0Item> initSet)
        {
            bool isChanged;

            do
            {
                isChanged = false;
                foreach (var item in initSet.ToArray())
                {
                    isChanged = m_infoManager.Productions[item.ProductionIndex].Accept(m_closureVisitor, new ClosureInfo(item.DotLocation, isChanged, initSet));
                }
            } while (isChanged);

            return initSet;
        }

        private ISet<LR0Item> GetGoto(IEnumerable<LR0Item> state, IProduction symbol)
        {
            ISet<LR0Item> resultSet = new HashSet<LR0Item>();
            var symbolIndex = m_infoManager.GetInfo(symbol).Index;

            foreach (var item in state)
            {
                var production = m_infoManager.Productions[item.ProductionIndex];
                var info = m_infoManager.GetInfo(production);

                var dotSymbols = production.Accept(m_dotSymbolVisitor, item.DotLocation);

                if (dotSymbols.Count == 1 && m_infoManager.GetInfo(dotSymbols[0]).Index == symbolIndex)
                {
                    resultSet.Add(new LR0Item(info.Index, item.DotLocation + 1));
                }
                else if (dotSymbols.Count == 2)
                {
                    if (m_infoManager.GetInfo(dotSymbols[0]).Index == symbolIndex || m_infoManager.GetInfo(dotSymbols[1]).Index == symbolIndex)
                    {
                        resultSet.Add(new LR0Item(info.Index, item.DotLocation + 1));
                    }
                }
            }

            return GetClosure(resultSet);
        }

        /// <summary>
        /// Generates dot commands to be visualized using graphviz
        /// </summary>
        /// <returns>A string contains dot commands</returns>
        public override string ToString()
        {
            StringBuilder dotCommand = new StringBuilder();

            dotCommand.AppendLine("digraph LR0 {");
            dotCommand.AppendLine("node[shape=record, fontname=Courier]");
            dotCommand.AppendLine("edge[fontname=Courier]");

            foreach (var state in m_states)
            {
                foreach (var edge in state.Edges)
                {
                    dotCommand.Append("state");
                    dotCommand.Append(edge.SourceStateIndex);
                    dotCommand.Append(" -> ");
                    dotCommand.Append("state");
                    dotCommand.Append(edge.TargetStateIndex);
                    dotCommand.Append("[label=\"");
                    //label of edge
                    dotCommand.Append((m_infoManager.Productions[edge.SymbolIndex] as ProductionBase).DebugName);
                    dotCommand.Append("\" ");

                    if (m_infoManager.Productions[edge.SymbolIndex].IsTerminal)
                    {
                        dotCommand.Append(",color=blue, fontcolor=blue");
                    }

                    dotCommand.AppendLine("];");
                }

                //state labels
                dotCommand.Append("state");
                dotCommand.Append(state.Index);
                dotCommand.Append("[label=\"{");
                dotCommand.Append(state.Index);
                dotCommand.Append("\\n");
                ItemStringVisitor isv = new ItemStringVisitor();
                foreach (var item in state.ItemSet)
                {
                    var itemStr = m_infoManager.Productions[item.ProductionIndex].Accept(isv, item.DotLocation);

                    dotCommand.Append(Escape(itemStr));

                    dotCommand.Append("\\n");
                }

                if (state.Reduces.Count > 0)
                {
                    dotCommand.Append('|');

                    foreach (var reduce in state.Reduces)
                    {
                        dotCommand.Append(Escape((reduce.ReduceTerminal as ProductionBase).DebugName));
                        dotCommand.Append(" Reduce ");
                        dotCommand.Append(Escape((reduce.ReduceProduction as ProductionBase).DebugName));
                        dotCommand.Append("\\n");
                    }
                }

                if (state.IsAcceptState)
                {
                    dotCommand.Append("| $ Accept \\n");
                }

                dotCommand.Append("}\"");
                dotCommand.AppendLine("];");
            }



            dotCommand.AppendLine("}");

            return dotCommand.ToString();
        }

        private static string Escape(string input)
        {
            StringBuilder replaceBuilder = new StringBuilder(input);

            replaceBuilder.Replace(">", "\\>");
            replaceBuilder.Replace("<", "\\<");
            replaceBuilder.Replace("\"", "\\\"");

            replaceBuilder.Replace("{", "\\{");
            replaceBuilder.Replace("}", "\\}");

            replaceBuilder.Replace("[", "\\[");
            replaceBuilder.Replace("]", "\\]");

            replaceBuilder.Replace("|", "\\|");

            return replaceBuilder.ToString();
        }
    }

}

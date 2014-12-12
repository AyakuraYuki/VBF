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

using System.Collections;
using System.Collections.Generic;
using VBF.Compilers.Scanners;

namespace VBF.Compilers.Parsers.Generator
{


    public class ActionListNode<T> : IEnumerable<T>
    {
        private ActionListNode<T> m_nextNode;

        public ActionListNode(T value)
        {
            Value = value;
        }

        public T Value { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            ActionListNode<T> currentNode = this;
            do
            {
                yield return currentNode.Value;

                currentNode = currentNode.m_nextNode;
            } while (currentNode != null);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public ActionListNode<T> Append(ActionListNode<T> next)
        {
            m_nextNode = next;

            return m_nextNode;
        }

        public ActionListNode<T> GetNext()
        {
            return m_nextNode;
        }

        internal static void AppendToLast(ref ActionListNode<T> list, T value)
        {
            if (list == null)
            {
                list = new ActionListNode<T>(value);
            }
            else
            {
                ActionListNode<T> head = list;

                while (head.m_nextNode != null)
                {
                    head = head.m_nextNode;
                }

                head.Append(new ActionListNode<T>(value));
            }

        }
    }

    public class TransitionTable
    {
        private int m_acceptProductionIndex;
        private int[,] m_gotoTable;
        private IProduction[] m_nonTerminals;
        private ActionListNode<int>[,] m_reduceTable;
        private ActionListNode<int>[,] m_shiftTable;
        private string[] m_tokenDescriptions;

        private TransitionTable(int tokenCount, int stateCount, int productionCount, string[] tokenDescriptions)
        {
            TokenCount = tokenCount;
            StateCount = stateCount;
            ProductionCount = productionCount;

            m_gotoTable = new int[stateCount, productionCount];
            m_shiftTable = new ActionListNode<int>[stateCount, tokenCount + 1];
            m_reduceTable = new ActionListNode<int>[stateCount, tokenCount + 1];
            m_tokenDescriptions = tokenDescriptions;
        }

        public int TokenCount { get; private set; }
        public int StateCount { get; private set; }
        public int ProductionCount { get; private set; }

        public IReadOnlyList<IProduction> NonTerminals
        {
            get
            {
                return m_nonTerminals;
            }
        }

        public ActionListNode<int> GetLexersInShifting(int state)
        {
            return m_shiftTable[state, TokenCount];
        }

        public ActionListNode<int> GetLexersInReducing(int state)
        {
            return m_reduceTable[state, TokenCount];
        }

        public ActionListNode<int> GetShift(int state, int tokenIndex)
        {
            return m_shiftTable[state, tokenIndex];
        }

        public int GetGoto(int state, int nonterminalIndex)
        {
            return m_gotoTable[state, nonterminalIndex];
        }

        public ActionListNode<int> GetReduce(int state, int tokenIndex)
        {
            return m_reduceTable[state, tokenIndex];
        }

        public string GetTokenDescription(int tokenIndex)
        {
            CodeContract.RequiresArgumentInRange(tokenIndex >= 0 && tokenIndex < m_tokenDescriptions.Length, "tokenIndex",
                "tokenIndex must be greater than or equal to 0 and less than the token count");

            return m_tokenDescriptions[tokenIndex];
        }

        public static TransitionTable Create(LR0Model model, ScannerInfo scannerInfo)
        {
            CodeContract.RequiresArgumentNotNull(model, "model");


            string[] tokenDescriptions = new string[scannerInfo.EndOfStreamTokenIndex + 1];
            List<IProduction> nonterminals = new List<IProduction>();
            foreach (var production in model.ProductionInfoManager.Productions)
            {
                if (!production.IsTerminal)
                {
                    var info = model.ProductionInfoManager.GetInfo(production);

                    info.NonTerminalIndex = nonterminals.Count;
                    nonterminals.Add(production);
                }
                else
                {
                    var terminal = (production as Terminal);

                    string description;
                    int index;

                    if (terminal != null)
                    {
                        index = terminal.Token.Index;
                        description = terminal.Token.Description;
                    }
                    else
                    {
                        index = scannerInfo.EndOfStreamTokenIndex;
                        description = "$";
                    }

                    tokenDescriptions[index] = description;
                }
            }

            //add one null reference to non-terminal list
            //for "accept" action in parsing
            nonterminals.Add(null);

            TransitionTable table = new TransitionTable(scannerInfo.EndOfStreamTokenIndex + 1, model.States.Count, nonterminals.Count, tokenDescriptions);
            table.m_nonTerminals = nonterminals.ToArray();
            table.m_acceptProductionIndex = nonterminals.Count - 1;

            for (int i = 0; i < model.States.Count; i++)
            {
                var state = model.States[i];

                foreach (var edge in state.Edges)
                {
                    var edgeSymbol = model.ProductionInfoManager.Productions[edge.SymbolIndex];
                    var info = model.ProductionInfoManager.GetInfo(edgeSymbol);

                    if (edgeSymbol.IsTerminal)
                    {
                        //shift
                        Terminal t = edgeSymbol as Terminal;
                        int tokenIndex = t == null ? scannerInfo.EndOfStreamTokenIndex : t.Token.Index;



                        ActionListNode<int>.AppendToLast(ref table.m_shiftTable[i, tokenIndex], edge.TargetStateIndex);
                    }
                    else
                    {
                        //goto
                        table.m_gotoTable[i, info.NonTerminalIndex] = edge.TargetStateIndex;
                    }
                }

                //lexer states for shifting
                if (state.MaxShiftingLexer != null)
                {
                    ActionListNode<int>.AppendToLast(ref table.m_shiftTable[i, table.TokenCount], state.MaxShiftingLexer.Value);
                }

                //reduces
                foreach (var reduce in state.Reduces)
                {
                    Terminal t = reduce.ReduceTerminal as Terminal;
                    int tokenIndex = t == null ? scannerInfo.EndOfStreamTokenIndex : t.Token.Index;

                    var info = model.ProductionInfoManager.GetInfo(reduce.ReduceProduction);

                    ActionListNode<int>.AppendToLast(ref table.m_reduceTable[i, tokenIndex], info.NonTerminalIndex);
                }

                //lexer states for reducing
                if (state.MaxReducingLexer != null)
                {
                    ActionListNode<int>.AppendToLast(ref table.m_reduceTable[i, table.TokenCount], state.MaxReducingLexer.Value);
                }

                //accepts
                if (state.IsAcceptState)
                {
                    ActionListNode<int>.AppendToLast(ref table.m_reduceTable[i, scannerInfo.EndOfStreamTokenIndex], table.m_acceptProductionIndex);
                }

            }

            return table;
        }
    }
}

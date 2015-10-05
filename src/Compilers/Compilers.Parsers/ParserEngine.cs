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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VBF.Compilers.Parsers.Generator;
using VBF.Compilers.Scanners;

namespace VBF.Compilers.Parsers
{
    public class ParserEngine
    {
        private const int c_panicRecoveryThreshold = 2048;

        private List<ParserHead> m_acceptedHeads;

        ParserHeadCleaner m_cleaner;
        private List<ParserHead> m_errorCandidates;

        private SyntaxErrors m_errorDef;
        private List<ParserHead> m_heads;
        private List<ParserHead> m_recoverReducedHeads;
        private List<ParserHead> m_reducedHeads;
        private ReduceVisitor m_reducer;
        private List<ParserHead> m_shiftedHeads;
        private List<ParserHead> m_tempHeads;
        private TransitionTable m_transitions;

        public ParserEngine(TransitionTable transitions, SyntaxErrors errorDef)
        {
            CodeContract.RequiresArgumentNotNull(transitions, "transitions");
            CodeContract.RequiresArgumentNotNull(errorDef, "errorDef");

            m_transitions = transitions;
            m_reducer = new ReduceVisitor(transitions);

            m_heads = new List<ParserHead>();
            m_shiftedHeads = new List<ParserHead>();
            m_acceptedHeads = new List<ParserHead>();
            m_errorCandidates = new List<ParserHead>();
            m_tempHeads = new List<ParserHead>();
            m_reducedHeads = new List<ParserHead>();
            m_recoverReducedHeads = new List<ParserHead>();

            m_errorDef = errorDef;

            m_cleaner = new ParserHeadCleaner();
            EnableDeletionRecovery = true;
            EnableInsertionRecovery = true;
            EnableReplacementRecovery = true;

            //init state
            m_heads.Add(new ParserHead(new StackNode(0, null, null)));
        }

        public int CurrentStackCount
        {
            get
            {
                return m_heads.Count;
            }
        }

        public int AcceptedCount
        {
            get
            {
                return m_acceptedHeads.Count;
            }
        }

        public bool EnableReplacementRecovery { get; set; }
        public bool EnableInsertionRecovery { get; set; }
        public bool EnableDeletionRecovery { get; set; }

        public object GetResult(int index, CompilationErrorList errorList)
        {
            CodeContract.RequiresArgumentInRange(index >= 0 && index < m_acceptedHeads.Count, "index", "index is out of range");

            var head = m_acceptedHeads[index];

            if (head.Errors != null && errorList != null)
            {
                //aggregate errors
                foreach (var error in head.Errors)
                {
                    int errorId = error.ErrorId ?? m_errorDef.OtherErrorId;

                    errorList.AddError(errorId, error.ErrorPosition, error.ErrorArgument, error.ErrorArgument2);
                }
            }

            return head.TopStackValue;
        }

        public ResultInfo GetResultInfo(int index)
        {
            return new ResultInfo(m_acceptedHeads[index].Errors == null ? 0 : m_acceptedHeads[index].Errors.Count);
        }

        public void Input(Lexeme z)
        {
            Input(z, Task.Factory.CancellationToken);
        }

        public void Input(Lexeme z, CancellationToken ctoken)
        {
            while (true)
            {
                var heads = m_heads;

                for (int i = 0; i < heads.Count; i++)
                {
                    var head = heads[i];

                    int stateNumber = head.TopStackStateIndex;


                    bool isShiftedOrReduced = false;

                    var shiftLexer = m_transitions.GetLexersInShifting(stateNumber);

                    int tokenIndex;
                    if (shiftLexer == null)
                    {
                        tokenIndex = z.TokenIndex;
                    }
                    else
                    {
                        tokenIndex = z.GetTokenIndex(shiftLexer.Value);
                    }

                    //get shift
                    var shifts = m_transitions.GetShift(stateNumber, tokenIndex);

                    //shifts
                    var shift = shifts;

                    while (shift != null)
                    {
                        ctoken.ThrowIfCancellationRequested();

                        isShiftedOrReduced = true;

                        var newHead = head.Clone();

                        newHead.Shift(z, shift.Value);

                        //save shifted heads
                        m_shiftedHeads.Add(newHead);

                        //get next shift
                        shift = shift.GetNext();
                    }



                    //reduces
                    var reduceLexer = m_transitions.GetLexersInReducing(stateNumber);

                    if (reduceLexer == null)
                    {
                        tokenIndex = z.TokenIndex;
                    }
                    else
                    {
                        tokenIndex = z.GetTokenIndex(reduceLexer.Value);
                    }

                    var reduces = m_transitions.GetReduce(stateNumber, tokenIndex);
                    var reduce = reduces;

                    while (reduce != null)
                    {
                        ctoken.ThrowIfCancellationRequested();

                        isShiftedOrReduced = true;

                        int productionIndex = reduce.Value;
                        IProduction production = m_transitions.NonTerminals[productionIndex];

                        var reducedHead = head.Clone();

                        reducedHead.Reduce(production, m_reducer, z);

                        if (reducedHead.IsAccepted)
                        {
                            m_acceptedHeads.Add(reducedHead);
                        }
                        else
                        {
                            //add back to queue, until shifted
                            m_reducedHeads.Add(reducedHead);
                        }

                        //get next reduce
                        reduce = reduce.GetNext();
                    }

                    if (!isShiftedOrReduced)
                    {
                        m_errorCandidates.Add(head);
                    }

                }

                if (m_reducedHeads.Count > 0)
                {
                    m_heads.Clear();
                    m_cleaner.CleanHeads(m_reducedHeads, m_heads);
                    m_reducedHeads.Clear();

                    continue;
                }
                else if (m_shiftedHeads.Count == 0 && m_acceptedHeads.Count == 0)
                {
                    //no action for current lexeme, error recovery
                    RecoverError(z, ctoken);
                }
                else
                {
                    break;
                }
            }

            CleanShiftedAndAcceptedHeads();
        }

        private void RecoverError(Lexeme z, CancellationToken ctoken)
        {
            List<ParserHead> shiftedHeads = m_shiftedHeads;

            m_heads.Clear();
            int errorHeadCount = m_errorCandidates.Count;

            Debug.Assert(errorHeadCount > 0);

            if (errorHeadCount > c_panicRecoveryThreshold)
            {
                PerformPanicRecovery(z, shiftedHeads);
            }

            for (int i = 0; i < errorHeadCount; i++)
            {
                ctoken.ThrowIfCancellationRequested();

                var head = m_errorCandidates[i];

                //restore stack before reduce, in case of an invalided reduce has been performed
                head.RestoreToLastShift();

                if (!z.IsEndOfStream)
                {
                    //option 1: remove
                    //remove current token and continue
                    if (EnableDeletionRecovery)
                    {
                        var deleteHead = head.Clone();

                        deleteHead.IncreaseErrorRecoverLevel();
                        deleteHead.AddError(new ErrorRecord(m_errorDef.TokenUnexpectedId, z.Value.Span) { ErrorArgument = z.Value });

                        shiftedHeads.Add(deleteHead); 
                    }

                    if (EnableReplacementRecovery)
                    {
                        //option 2: replace
                        //replace the current input char with all possible shifts token and continue
                        ReduceAndShiftForRecovery(z, head, shiftedHeads, m_errorDef.TokenMistakeId, ctoken); 
                    }
                }

                if (EnableInsertionRecovery)
                {
                    //option 3: insert
                    //insert all possible shifts token and continue
                    ReduceAndShiftForRecovery(z, head, m_heads, m_errorDef.TokenMissingId, ctoken); 
                }
                else if(z.IsEndOfStream)
                {
                    //no other choices
                    PerformPanicRecovery(z, shiftedHeads);
                }
            }
        }

        private void PerformPanicRecovery(Lexeme z, List<ParserHead> shiftedHeads)
        {
            //Panic recovery
            //to the 1st head:
            //pop stack until there's a state S, which has a Goto action of a non-terminal A
            //discard input until there's an token a in Follow(A)
            //push Goto(s, A) into stack
            //discard all other heads

            m_heads.Clear();
            m_heads.AddRange(shiftedHeads.Where(h => h.ErrorRecoverLevel == 0));
            shiftedHeads.Clear();

            ParserHead errorHead1 = m_errorCandidates[0];
            m_errorCandidates.Clear();

            var candidates = errorHead1.PanicRecover(m_transitions, z.Value.Span, z.IsEndOfStream);

            ISet<IProduction> follows = new HashSet<IProduction>();

            foreach (var candidate in candidates)
            {
                ProductionBase p = candidate.Item2 as ProductionBase;
                follows.UnionWith(p.Info.Follow);

                m_heads.Add(candidate.Item1);
            }
            if (m_heads.Count > 0)
            {
                throw new PanicRecoverException(follows);
            }
            else
            {
                throw new ParsingFailureException("There's no way to recover from parser error");
            }
        }

        private void ReduceAndShiftForRecovery(Lexeme z, ParserHead head, IList<ParserHead> shiftTarget, int syntaxError, CancellationToken ctoken)
        {
            Queue<ParserHead> recoverQueue = new Queue<ParserHead>();

            for (int j = 0; j < m_transitions.TokenCount - 1; j++)
            {
                recoverQueue.Enqueue(head);

                while (recoverQueue.Count > 0)
                {
                    var recoverHead = recoverQueue.Dequeue();                   

                    int recoverStateNumber = recoverHead.TopStackStateIndex;

                    var shiftLexer = m_transitions.GetLexersInShifting(recoverStateNumber);

                    var recoverShifts = m_transitions.GetShift(recoverStateNumber, j);
                    var recoverShift = recoverShifts;

                    while (recoverShift != null)
                    {
                        ctoken.ThrowIfCancellationRequested();

                        var insertHead = recoverHead.Clone();

                        var insertLexeme = z.GetErrorCorrectionLexeme(j, m_transitions.GetTokenDescription(j));
                        insertHead.Shift(insertLexeme, recoverShift.Value);
                        insertHead.IncreaseErrorRecoverLevel();
                        insertHead.AddError(new ErrorRecord(syntaxError, z.Value.Span) 
                        { 
                            ErrorArgument = insertLexeme.Value,
                            ErrorArgument2 = z.Value
                        });

                        shiftTarget.Add(insertHead);

                        recoverShift = recoverShift.GetNext();
                    }

                    var reduceLexer = m_transitions.GetLexersInReducing(recoverStateNumber);

                    var recoverReduces = m_transitions.GetReduce(recoverStateNumber, j);
                    var recoverReduce = recoverReduces;

                    while (recoverReduce != null)
                    {
                        ctoken.ThrowIfCancellationRequested();

                        int productionIndex = recoverReduce.Value;
                        IProduction production = m_transitions.NonTerminals[productionIndex];

                        var reducedHead = recoverHead.Clone();

                        reducedHead.Reduce(production, m_reducer, z);

                        //add back to queue, until shifted
                        m_recoverReducedHeads.Add(reducedHead);

                        //get next reduce
                        recoverReduce = recoverReduce.GetNext();
                    }

                    if (m_recoverReducedHeads.Count > 0)
                    {
                        m_tempHeads.Clear();
                        m_cleaner.CleanHeads(m_recoverReducedHeads, m_tempHeads);
                        m_recoverReducedHeads.Clear();

                        foreach (var recoveredHead in m_tempHeads)
                        {
                            recoverQueue.Enqueue(recoveredHead);
                        }
                    }
                }
            }
        }

        private void CleanShiftedAndAcceptedHeads()
        {
            m_heads.Clear();
            m_errorCandidates.Clear();
            m_tempHeads.Clear();

            if (m_acceptedHeads.Count > 0)
            {
                m_cleaner.CleanHeads(m_acceptedHeads, m_tempHeads);
                m_acceptedHeads.Clear();

                var swap = m_tempHeads;
                m_tempHeads = m_acceptedHeads;
                m_acceptedHeads = swap;
            }

            if (m_shiftedHeads.Count > 0)
            {
                m_cleaner.CleanHeads(m_shiftedHeads, m_heads);
                m_shiftedHeads.Clear();
            }

        }

    }
}

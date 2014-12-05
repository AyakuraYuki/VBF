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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VBF.Compilers.Parsers.Generator;
using VBF.Compilers.Scanners;

namespace VBF.Compilers.Parsers
{
    /// <summary>
    /// Provides a parser framework for parser writers. Inherit this class to begin.
    /// </summary>
    /// <typeparam name="T">The type of parser result</typeparam>
    public abstract class ParserBase<T>
    {
        private SyntaxErrors m_errorDefinition;
        private CompilationErrorManager m_errorManager;
        private bool m_isInitialized = false;

        private Lexicon m_lexicon;
        //private Scanner m_scanner;
        private ProductionInfoManager m_productionInfoManager;
        private ScannerInfo m_scannerInfo;
        private TransitionTable m_transitionTable;

        private List<Token> m_triviaTokens;

        protected ParserBase(CompilationErrorManager errorManager)
        {
            CodeContract.RequiresArgumentNotNull(errorManager, "errorManager");

            m_errorManager = errorManager;
            m_errorDefinition = new SyntaxErrors()
            {
                LexicalErrorId = 101,
                TokenUnexpectedId = 201,
                TokenMissingId = 202,
                TokenMistakeId = 203,
                OtherErrorId = 200
            };

            m_triviaTokens = new List<Token>();

            EnableReplacementRecovery = true;
            EnableInsertionRecovery = true;
            EnableDeletionRecovery = true;
        }

        public CompilationErrorManager ErrorManager
        {
            get { return m_errorManager; }
        }

        public bool IsInitialized
        {
            get { return m_isInitialized; }
        }

        protected Lexicon Lexicon { get { return m_lexicon; } }
        protected ScannerInfo ScannerInfo { get { return m_scannerInfo; } }
        protected ProductionInfoManager ProductionInfoManager { get { return m_productionInfoManager; } }
        protected SyntaxErrors ErrorDefinitions { get { return m_errorDefinition; } }
        //protected Scanner Scanner { get { return m_scanner; } }

        public bool EnableReplacementRecovery { get; set; }
        public bool EnableInsertionRecovery { get; set; }
        public bool EnableDeletionRecovery { get; set; }

        /// <summary>
        /// Implement this method to define lexer
        /// </summary>
        /// <param name="lexicon">The lexicon used to define tokens.</param>
        /// <param name="triviaTokens">Tokens added to this list will be automatically bypassed in parsing.</param>
        protected abstract void OnDefineLexer(Lexicon lexicon, ICollection<Token> triviaTokens);

        /// <summary>
        /// Implement this method to define grammar
        /// </summary>
        /// <returns>The root production of the grammar.</returns>
        protected abstract ProductionBase<T> OnDefineGrammar();

        public void Initialize()
        {
            if (!m_isInitialized)
            {
                OnInitialize();
            }
        }

        private void OnInitialize()
        {
            m_lexicon = new Lexicon();

            OnDefineLexer(m_lexicon, m_triviaTokens);

            m_scannerInfo = OnCreateScannerInfo();

            var production = OnDefineGrammar();

            if (production == null)
            {
                throw new InvalidOperationException("Root producton is not defined");
            }

            var rootProduction = production.SuffixedBy(Grammar.Eos());
            m_productionInfoManager = new ProductionInfoManager(rootProduction);

            m_transitionTable = OnCreateTransitionTable(OnCreateAutomaton(m_productionInfoManager), m_scannerInfo);

            OnDefineParserErrors(m_errorDefinition, m_errorManager);

            m_isInitialized = true;
        }


        protected virtual LR0Model OnCreateAutomaton(ProductionInfoManager productionInfoManager)
        {
            var automaton = new LR0Model(productionInfoManager);
            automaton.BuildModel();

            return automaton;
        }

        protected virtual TransitionTable OnCreateTransitionTable(LR0Model automaton, ScannerInfo scannerInfo)
        {
            return TransitionTable.Create(automaton, scannerInfo);
        }

        protected virtual ScannerInfo OnCreateScannerInfo()
        {
            return m_lexicon.CreateScannerInfo();
        }

        protected virtual void OnDefineParserErrors(SyntaxErrors errorDefinition, CompilationErrorManager errorManager)
        {
            errorManager.DefineError(errorDefinition.LexicalErrorId, 0, CompilationStage.Scanning, "Invalid token: '{0}'");
            errorManager.DefineError(errorDefinition.TokenUnexpectedId, 0, CompilationStage.Parsing, "Unexpected token: '{0}'");
            errorManager.DefineError(errorDefinition.TokenMissingId, 0, CompilationStage.Parsing, "Missing token: '{0}'");
            errorManager.DefineError(errorDefinition.TokenMistakeId, 0, CompilationStage.Parsing, "Invalid token: '{1}', did you mean: '{0}' ?");
            errorManager.DefineError(errorDefinition.OtherErrorId, 0, CompilationStage.Parsing, "Syntax error");
        }

        public T Parse(SourceReader source, CompilationErrorList errorList)
        {
            return Parse(source, errorList, Task.Factory.CancellationToken);
        }

        public T Parse(SourceReader source, CompilationErrorList errorList, CancellationToken ctoken)
        {
            CodeContract.RequiresArgumentNotNull(source, "source");

            if (!m_isInitialized)
            {
                OnInitialize();
            }

            Scanner scanner;

            scanner = new Scanner(m_scannerInfo);
            scanner.SetTriviaTokens(m_triviaTokens.Select(t => t.Index).ToArray());
            scanner.ErrorList = errorList;
            scanner.RecoverErrors = true;
            scanner.LexicalErrorId = m_errorDefinition.LexicalErrorId;

            scanner.SetSource(source);

            ParserEngine engine = new ParserEngine(m_transitionTable, m_errorDefinition);

            engine.EnableDeletionRecovery = EnableDeletionRecovery;
            engine.EnableInsertionRecovery = EnableInsertionRecovery;
            engine.EnableReplacementRecovery = EnableReplacementRecovery;

            Lexeme r = scanner.Read();
            while (true)
            {
                try
                {

                    engine.Input(r, ctoken);
                }
                catch (PanicRecoverException prex)
                {
                    var follow = prex.PossibleFollow;

                    HashSet<int> validTokens = new HashSet<int>(follow.Select(p =>
                    {
                        Terminal t = p as Terminal;
                        if (t != null)
                        {
                            return t.Token.Index;
                        }
                        else
                        {
                            return m_scannerInfo.EndOfStreamTokenIndex;
                        }


                    }));

                    while (!validTokens.Contains(r.TokenIndex) && !r.IsEndOfStream)
                    {
                        r = scanner.Read();
                    }

                    continue;
                }

                if (r.IsEndOfStream)
                {
                    break;
                }

                r = scanner.Read();
            }

            if (engine.AcceptedCount == 0)
            {
                throw new ParsingFailureException("There's no parsing result");
            }

            if (engine.AcceptedCount > 1 && engine.GetResultInfo(0).ErrorCount == 0)
            {
                throw new ParsingFailureException("Multiple parsing results are found. There's ambiguity in your grammar");
            }

            object result = engine.GetResult(0, errorList);

            return (T)result;
        }

        public T Parse(TextReader source, CompilationErrorList errorList)
        {
            CodeContract.RequiresArgumentNotNull(source, "source");
            return Parse(new SourceReader(source), errorList);
        }

        public T Parse(TextReader source, CompilationErrorList errorList, CancellationToken ctoken)
        {
            CodeContract.RequiresArgumentNotNull(source, "source");
            return Parse(new SourceReader(source), errorList, ctoken);
        }

        public T Parse(string source, CompilationErrorList errorList)
        {
            CodeContract.RequiresArgumentNotNull(source, "source");
            return Parse(new StringReader(source), errorList);
        }

        public T Parse(string source, CompilationErrorList errorList, CancellationToken ctoken)
        {
            CodeContract.RequiresArgumentNotNull(source, "source");
            return Parse(new StringReader(source), errorList, ctoken);
        }
    }
}

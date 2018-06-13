﻿using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Rubberduck.Inspections.Abstract;
using Rubberduck.Inspections.Results;
using Rubberduck.Parsing;
using Rubberduck.Parsing.Grammar;
using Rubberduck.Parsing.Inspections.Abstract;
using Rubberduck.Resources.Inspections;
using Rubberduck.Parsing.VBA;
using Rubberduck.VBEditor;
using System.Collections.Generic;
using System.Linq;
using Rubberduck.Parsing.Symbols;
using System;

namespace Rubberduck.Inspections.Concrete.UnreachableCaseInspection
{
    public sealed class UnreachableCaseInspection : ParseTreeInspectionBase
    {
        private readonly IUnreachableCaseInspectorFactory _unreachableCaseInspectorFactory;
        private readonly IParseTreeValueFactory _valueFactory;

        private enum CaseInpectionResult { Unreachable, MismatchType, CaseElse };

        private static readonly Dictionary<CaseInpectionResult, string> ResultMessages = new Dictionary<CaseInpectionResult, string>()
        {
            [CaseInpectionResult.Unreachable] = InspectionResults.UnreachableCaseInspection_Unreachable,
            [CaseInpectionResult.MismatchType] = InspectionResults.UnreachableCaseInspection_TypeMismatch,
            [CaseInpectionResult.CaseElse] = InspectionResults.UnreachableCaseInspection_CaseElse
        };

        public UnreachableCaseInspection(RubberduckParserState state) : base(state)
        {
            //TODO_Question: IUnreachableCaseInspectionFactoryFactory - candidate for IoCInstaller?
            var factoryProvider = new UnreachableCaseInspectionFactoryProvider();

            _unreachableCaseInspectorFactory = factoryProvider.CreateIUnreachableInspectorFactory();
            _valueFactory = factoryProvider.CreateIParseTreeValueFactory();
        }

        public override IInspectionListener Listener { get; } =
            new UnreachableCaseInspectionListener();

        private List<IInspectionResult> _inspectionResults = new List<IInspectionResult>();
        private ParseTreeVisitorResults ValueResults { get; }  = new ParseTreeVisitorResults();

        protected override IEnumerable<IInspectionResult> DoGetInspectionResults()
        {
            _inspectionResults = new List<IInspectionResult>();
            var qualifiedSelectCaseStmts = Listener.Contexts
                .Where(result => !IsIgnoringInspectionResultFor(result.ModuleName, result.Context.Start.Line));

            var parseTreeValueVisitor = CreateParseTreeValueVisitor(_valueFactory, GetIdentifierReferenceForContext);
            parseTreeValueVisitor.OnValueResultCreated += ValueResults.OnNewValueResult;

            foreach (var qualifiedSelectCaseStmt in qualifiedSelectCaseStmts)
            {
                qualifiedSelectCaseStmt.Context.Accept(parseTreeValueVisitor);
                var selectCaseInspector = _unreachableCaseInspectorFactory.Create((VBAParser.SelectCaseStmtContext)qualifiedSelectCaseStmt.Context, ValueResults, _valueFactory, GetVariableTypeName);

                selectCaseInspector.InspectForUnreachableCases();

                selectCaseInspector.UnreachableCases.ForEach(uc => CreateInspectionResult(qualifiedSelectCaseStmt, uc, ResultMessages[CaseInpectionResult.Unreachable]));
                selectCaseInspector.MismatchTypeCases.ForEach(mm => CreateInspectionResult(qualifiedSelectCaseStmt, mm, ResultMessages[CaseInpectionResult.MismatchType]));
                selectCaseInspector.UnreachableCaseElseCases.ForEach(ce => CreateInspectionResult(qualifiedSelectCaseStmt, ce, ResultMessages[CaseInpectionResult.CaseElse]));
            }
            return _inspectionResults;
        }

        private void CreateInspectionResult(QualifiedContext<ParserRuleContext> selectStmt, ParserRuleContext unreachableBlock, string message)
        {
            var result = new QualifiedContextInspectionResult(this,
                        message,
                        new QualifiedContext<ParserRuleContext>(selectStmt.ModuleName, unreachableBlock));
            _inspectionResults.Add(result);
        }

        public static IParseTreeValueVisitor CreateParseTreeValueVisitor(IParseTreeValueFactory valueFactory, Func<ParserRuleContext, (bool success, IdentifierReference idRef)> func)
        {
            return new ParseTreeValueVisitor(valueFactory, func);
        }

        //Method is used as a delegate to avoid propogating RubberduckParserState beyond this class
        private (bool success, IdentifierReference idRef) GetIdentifierReferenceForContext(ParserRuleContext context)
        {
            return GetIdentifierReferenceForContext(context, State);
        }

        //public static to support tests
        public static (bool success, IdentifierReference idRef) GetIdentifierReferenceForContext(ParserRuleContext context, RubberduckParserState state)
        {
            IdentifierReference idRef = null;
            var success = false;
            var identifierReferences = (state.DeclarationFinder.MatchName(context.GetText()).Select(dec => dec.References)).SelectMany(rf => rf)
                .Where(rf => rf.Context == context);
            if (identifierReferences.Count() == 1)
            {
                idRef = identifierReferences.First();
                success = true;
            }
            return (success, idRef);
        }

        //Method is used as a delegate to avoid propogating RubberduckParserState beyond this class
        private string GetVariableTypeName(string variableName, ParserRuleContext ancestor)
        {
            var descendents = ancestor.GetDescendents<VBAParser.SimpleNameExprContext>().Where(desc => desc.GetText().Equals(variableName));
            if (descendents.Any())
            {
                (bool success, IdentifierReference idRef) = GetIdentifierReferenceForContext(descendents.First(), State);
                if (success)
                {
                    return idRef.Declaration.AsTypeName;
                }
            }
            return string.Empty;
        }

        #region UnreachableCaseInspectionListeners
        public class UnreachableCaseInspectionListener : VBAParserBaseListener, IInspectionListener
        {
            private readonly List<QualifiedContext<ParserRuleContext>> _contexts = new List<QualifiedContext<ParserRuleContext>>();
            public IReadOnlyList<QualifiedContext<ParserRuleContext>> Contexts => _contexts;

            public QualifiedModuleName CurrentModuleName { get; set; }

            public void ClearContexts()
            {
                _contexts.Clear();
            }

            public override void EnterSelectCaseStmt([NotNull] VBAParser.SelectCaseStmtContext context)
            {
                _contexts.Add(new QualifiedContext<ParserRuleContext>(CurrentModuleName, context));
            }
        }
        #endregion
    }
}
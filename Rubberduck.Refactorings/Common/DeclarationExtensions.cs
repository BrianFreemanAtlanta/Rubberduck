﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Antlr4.Runtime;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Parsing.VBA;
using Rubberduck.Resources;
using Rubberduck.VBEditor;
using Rubberduck.VBEditor.SafeComWrappers;

// ReSharper disable LocalizableElement

namespace Rubberduck.Common
{
    public static class DeclarationExtensions
    {

        public static string ToLocalizedString(this DeclarationType type)
        {
            return RubberduckUI.ResourceManager.GetString("DeclarationType_" + type, CultureInfo.CurrentUICulture);
        }

        public static readonly DeclarationType[] ProcedureTypes =
        {
            DeclarationType.Procedure,
            DeclarationType.Function,
            DeclarationType.PropertyGet,
            DeclarationType.PropertyLet,
            DeclarationType.PropertySet
        };

        /// <summary>
        /// Gets all declarations of the specified <see cref="DeclarationType"/>.
        /// </summary>
        public static IEnumerable<Declaration> OfType(this IEnumerable<Declaration> declarations, DeclarationType declarationType)
        {
            return declarations.Where(declaration =>
                declaration.DeclarationType == declarationType);
        }

        /// <summary>
        /// Gets all declarations of any one of the specified <see cref="DeclarationType"/> values.
        /// </summary>
        public static IEnumerable<Declaration> OfType(this IEnumerable<Declaration> declarations, params DeclarationType[] declarationTypes)
        {
            return declarations.Where(declaration =>
                declarationTypes.Any(type => declaration.DeclarationType == type));
        }

        public static IEnumerable<Declaration> Named(this IEnumerable<Declaration> declarations, string name)
        {
            return declarations.Where(declaration => declaration.IdentifierName == name);
        }

        /// <summary>
        /// Gets the declaration for all identifiers declared in or below the specified scope.
        /// </summary>
        public static IEnumerable<Declaration> InScope(this IEnumerable<Declaration> declarations, string scope)
        {
            return string.IsNullOrEmpty(scope) 
                ? declarations 
                : declarations.Where(declaration => declaration.Scope.StartsWith(scope));
        }

        /// <summary>
        /// Gets the declaration for all identifiers declared in or below the specified scope.
        /// </summary>
        public static IEnumerable<Declaration> InScope(this IEnumerable<Declaration> declarations, Declaration parent)
        {
            return declarations.Where(declaration => declaration.ParentScope == parent.Scope);
        }

        /// <summary>
        /// Finds all event handler procedures for specified control declaration.
        /// </summary>
        public static IEnumerable<Declaration> FindEventHandlers(this IEnumerable<Declaration> declarations, Declaration control)
        {
            Debug.Assert(control.DeclarationType == DeclarationType.Control);

            return declarations.Where(declaration => declaration.ParentScope == control.ParentScope
                && declaration.DeclarationType == DeclarationType.Procedure
                && declaration.IdentifierName.StartsWith(control.IdentifierName + "_"));
        }

        public static IEnumerable<Declaration> FindUserEventHandlers(this IEnumerable<Declaration> declarations)
        {
            var declarationList = declarations.ToList();

            var userEvents =
                declarationList.Where(item => item.IsUserDefined && item.DeclarationType == DeclarationType.Event).ToList();

            var handlers = new List<Declaration>();
            foreach (var @event in userEvents)
            {
                handlers.AddRange(declarationList.FindHandlersForEvent(@event).Select(s => s.Item2));
            }
            
            return handlers;
        }

        /// <summary>
        /// Gets the <see cref="Declaration"/> of the specified <see cref="DeclarationType"/>, 
        /// at the specified <see cref="QualifiedSelection"/>.
        /// Returns the declaration if selection is on an identifier reference.
        /// </summary>
        public static Declaration FindSelectedDeclaration(this IEnumerable<Declaration> declarations, QualifiedSelection selection, DeclarationType type, Func<Declaration, Selection> selector = null)
        {
            return FindSelectedDeclaration(declarations, selection, new[] { type }, selector);
        }

        /// <summary>
        /// Gets the <see cref="Declaration"/> of the specified <see cref="DeclarationType"/>, 
        /// at the specified <see cref="QualifiedSelection"/>.
        /// Returns the declaration if selection is on an identifier reference.
        /// </summary>
        public static Declaration FindSelectedDeclaration(this IEnumerable<Declaration> declarations, QualifiedSelection selection, IEnumerable<DeclarationType> types, Func<Declaration, Selection> selector = null)
        {
            var userDeclarations = declarations.Where(item => item.IsUserDefined);
            var items = userDeclarations.Where(item => types.Contains(item.DeclarationType)
                && item.QualifiedName.QualifiedModuleName == selection.QualifiedName).ToList();

            var declaration = items.SingleOrDefault(item =>
                selector?.Invoke(item).Contains(selection.Selection) ?? item.Selection.Contains(selection.Selection));

            if (declaration != null)
            {
                return declaration;
            }

            // if we haven't returned yet, then we must be on an identifier reference.
            declaration = items.SingleOrDefault(item => item.IsUserDefined
                && types.Contains(item.DeclarationType)
                && item.References.Any(reference =>
                reference.QualifiedModuleName == selection.QualifiedName
                && reference.Selection.Contains(selection.Selection)));

            return declaration;
        }

        public static IEnumerable<Declaration> FindFormEventHandlers(this RubberduckParserState state)
        {
            var items = state.AllDeclarations.ToList();

            var forms = items.Where(item => item.DeclarationType == DeclarationType.ClassModule
                && item.QualifiedName.QualifiedModuleName.ComponentType == ComponentType.UserForm)
                .ToList();

            var result = new List<Declaration>();
            foreach (var declaration in forms)
            {
                result.AddRange(FindFormEventHandlers(state, declaration));
            }

            return result;
        }

        public static IEnumerable<Declaration> FindFormEventHandlers(this RubberduckParserState state, Declaration userForm)
        {
            var items = state.AllDeclarations.ToList();
            var events = items.Where(item => !item.IsUserDefined
                                                     && item.ParentScope == "FM20.DLL;MSForms.FormEvents"
                                                     && item.DeclarationType == DeclarationType.Event).ToList();

            var handlerNames = events.Select(item => "UserForm_" + item.IdentifierName);
            var handlers = items.Where(item => item.ParentScope == userForm.Scope
                                                       && item.DeclarationType == DeclarationType.Procedure
                                                       && handlerNames.Contains(item.IdentifierName));

            return handlers.ToList();
        }

            /// <summary>
        /// Gets a tuple containing the <c>WithEvents</c> declaration and the corresponding handler,
        /// for each type implementing this event.
        /// </summary>
        public static IEnumerable<Tuple<Declaration,Declaration>> FindHandlersForEvent(this IEnumerable<Declaration> declarations, Declaration eventDeclaration)
        {
            var items = declarations as IList<Declaration> ?? declarations.ToList();
            return items.Where(item => item.IsWithEvents && item.AsTypeName == eventDeclaration.ComponentName)
            .Select(item => new
            {
                WithEventDeclaration = item, 
                EventProvider = items.SingleOrDefault(type => type.DeclarationType.HasFlag(DeclarationType.ClassModule) && type.QualifiedName.QualifiedModuleName == item.QualifiedName.QualifiedModuleName)
            })
            .Select(item => new
            {
                WithEventsDeclaration = item.WithEventDeclaration,
                ProviderEvents = items.Where(member => member.DeclarationType == DeclarationType.Event && member.QualifiedSelection.QualifiedName == item.EventProvider.QualifiedName.QualifiedModuleName)
            })
            .Select(item => Tuple.Create(
                item.WithEventsDeclaration,
                items.SingleOrDefault(declaration => declaration.DeclarationType == DeclarationType.Procedure
                && declaration.QualifiedName.QualifiedModuleName == item.WithEventsDeclaration.QualifiedName.QualifiedModuleName
                && declaration.IdentifierName == item.WithEventsDeclaration.IdentifierName + '_' + eventDeclaration.IdentifierName)
                ));
        }

        public static IEnumerable<Declaration> FindEventProcedures(this IEnumerable<Declaration> declarations, Declaration withEventsDeclaration)
        {
            if (!withEventsDeclaration.IsWithEvents)
            {
                return new Declaration[]{};
            }

            var items = declarations as IList<Declaration> ?? declarations.ToList();
            var type = withEventsDeclaration.AsTypeDeclaration;

            if (type == null)
            {
                return new Declaration[]{};
            }

            var members = GetTypeMembers(items, type).ToList();
            var events = members.Where(member => member.DeclarationType == DeclarationType.Event);
            var handlerNames = events.Select(e => withEventsDeclaration.IdentifierName + '_' + e.IdentifierName);

            return items.Where(item => item.Project != null 
                                               && item.ProjectId == withEventsDeclaration.ProjectId
                                               && item.ParentScope == withEventsDeclaration.ParentScope
                                               && item.DeclarationType == DeclarationType.Procedure
                                               && handlerNames.Any(name => item.IdentifierName == name))
                .ToList();
        }

        private static IEnumerable<Declaration> GetTypeMembers(this IEnumerable<Declaration> declarations, Declaration type)
        {
            return declarations.Where(item => Equals(item.ParentScopeDeclaration, type));
        }

        /// <summary>
        /// Returns the declaration contained in a qualified selection.
        /// To get the selection of a variable or field, use FindVariable(QualifiedSelection)
        /// </summary>
        /// <param name="declarations"></param>
        /// <param name="selection"></param>
        /// <param name="validDeclarationTypes"></param>
        /// <returns></returns>
        public static Declaration FindTarget(this IEnumerable<Declaration> declarations, QualifiedSelection selection, DeclarationType[] validDeclarationTypes)
        {
            var items = declarations.ToList();

            // TODO: Due to the new binding mechanism this can have more than one match (e.g. in the case of index expressions + simple name expressions)
            // Left as is for now because the binding is not fully integrated yet.
            var target = items
                .Where(item => item.IsUserDefined && validDeclarationTypes.Contains(item.DeclarationType))
                .FirstOrDefault(item => item.IsSelected(selection)
                                     || item.References.Any(r => r.IsSelected(selection)));

            if (target != null)
            {
                return target;
            }

            var targets = items
                .Where(item => item.IsUserDefined
                               && item.ComponentName == selection.QualifiedName.ComponentName
                               && validDeclarationTypes.Contains(item.DeclarationType));

            var currentSelection = new Selection(0, 0, int.MaxValue, int.MaxValue);

            foreach (var declaration in targets.Where(item => item.Context != null))
            {
                var activeSelection = new Selection(declaration.Context.Start.Line,
                                                    declaration.Context.Start.Column,
                                                    declaration.Context.Stop.Line,
                                                    declaration.Context.Stop.Column);

                if (currentSelection.Contains(activeSelection) && activeSelection.Contains(selection.Selection))
                {
                    target = declaration;
                    currentSelection = activeSelection;
                }

                foreach (var reference in declaration.References)
                {
                    var proc = (ParserRuleContext)reference.Context.Parent;
                    var paramList = proc ;

                    if (paramList == null)
                    {
                        continue;
                    }

                    activeSelection = new Selection(paramList.Start.Line,
                                                    paramList.Start.Column,
                                                    paramList.Stop.Line,
                                                    paramList.Stop.Column + paramList.Stop.Text.Length + 1);

                    if (currentSelection.Contains(activeSelection) && activeSelection.Contains(selection.Selection))
                    {
                        target = reference.Declaration;
                        currentSelection = activeSelection;
                    }
                }
            }
            return target;
        }
    }
}

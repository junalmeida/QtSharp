﻿using System.Collections.Generic;
using System.Linq;
using CppSharp.AST;
using CppSharp.Passes;
using QtSharp.DocGeneration;

namespace QtSharp
{
    public class GetCommentsFromQtDocsPass : TranslationUnitPass
    {
        public GetCommentsFromQtDocsPass(string docsPath, IEnumerable<string> modules)
        {
            this.documentation = new Documentation(docsPath, modules);
            // this.VisitOptions.VisitClassFields = false;
            // this.VisitOptions.VisitClassTemplateSpecializations = false;
            // this.VisitOptions.VisitFunctionReturnType = false;
            // this.VisitOptions.VisitFunctionParameters = false;
            // this.VisitOptions.VisitEventParameters = false;
            // this.VisitOptions.VisitClassBases = false;
            // this.VisitOptions.VisitTemplateArguments = false;
        }

        public override bool VisitASTContext(ASTContext context)
        {
            return this.documentation.Exists && base.VisitASTContext(context);
        }

        public override bool VisitTranslationUnit(TranslationUnit unit)
        {
            return !unit.IsSystemHeader && unit.IsGenerated && base.VisitTranslationUnit(unit);
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!@class.IsIncomplete && base.VisitClassDecl(@class))
            {
                if (@class.IsInterface)
                {
                    @class.Comment = @class.OriginalClass.Comment;
                    foreach (var method in @class.OriginalClass.Methods)
                    {
                        var interfaceMethod = @class.Methods.FirstOrDefault(m => m.OriginalPtr == method.OriginalPtr);
                        if (interfaceMethod != null)
                        {
                            interfaceMethod.Comment = method.Comment;
                        }
                    }
                    foreach (var property in @class.OriginalClass.Properties)
                    {
                        var interfaceProperty = @class.Properties.FirstOrDefault(p => p.Name == property.Name);
                        if (interfaceProperty != null)
                        {
                            interfaceProperty.Comment = property.Comment;
                        }
                    }
                }
                else
                {
                    this.documentation.DocumentType(@class);
                }
                return true;
            }
            return false;
        }

        public override bool VisitDeclarationContext(DeclarationContext context)
        {
            return context.IsGenerated && !context.TranslationUnit.IsSystemHeader && base.VisitDeclarationContext(context);
        }

        public override bool VisitEnumDecl(Enumeration @enum)
        {
            if (!base.VisitEnumDecl(@enum))
            {
                return false;
            }
            if (@enum.IsGenerated)
            {
                this.documentation.DocumentEnum(@enum);
                return true;
            }
            return false;
        }

        public override bool VisitFunctionDecl(Function function)
        {
            if (!base.VisitFunctionDecl(function) || function.TranslationUnit.IsSystemHeader || function.IsImplicit)
            {
                return false;
            }
            if (function.IsGenerated)
            {
                var @class = function.OriginalNamespace as Class;
                if (@class != null && @class.IsInterface)
                {
                    if (functionsComments.ContainsKey(function.Mangled))
                    {
                        function.Comment = new RawComment { BriefText = functionsComments[function.Mangled] };
                    }
                }
                else
                {
                    this.DocumentFunction(function);
                }
                return true;
            }
            return false;
        }

        public override bool VisitProperty(Property property)
        {
            if (!base.VisitProperty(property) || property.TranslationUnit.IsSystemHeader)
            {
                return false;
            }
            if (!property.IsSynthetized && property.IsGenerated)
            {
                foreach (var @class in from m in new[] { property.GetMethod, property.SetMethod }
                                       where m != null
                                       let @class = m.OriginalNamespace as Class
                                       where @class != null && @class.IsInterface
                                       select @class)
                {
                    RawComment comment = null;
                    if (property.GetMethod != null && functionsComments.ContainsKey(property.GetMethod.Mangled))
                    {
                        comment = new RawComment { BriefText = functionsComments[property.GetMethod.Mangled] };
                    }
                    if (comment == null && property.SetMethod != null && functionsComments.ContainsKey(property.SetMethod.Mangled))
                    {
                        comment = new RawComment { BriefText = functionsComments[property.SetMethod.Mangled] };
                    }
                    property.Comment = comment;
                    if (property.Comment != null)
                    {
                        return true;
                    }
                }
                this.documentation.DocumentProperty(property);
                if (property.Comment != null)
                {
                    if (property.GetMethod != null)
                    {
                        functionsComments[property.GetMethod.Mangled] = property.Comment.BriefText;
                    }
                    else
                    {
                        if (property.SetMethod != null)
                        {
                            functionsComments[property.SetMethod.Mangled] = property.Comment.BriefText;
                        }
                    }
                }
                return true;
            }
            return false;
        }

        public override bool VisitEvent(Event @event)
        {
            if (!base.VisitEvent(@event))
            {
                return false;
            }
            var function = @event.OriginalDeclaration as Function;
            if (function != null && @event.IsGenerated)
            {
                this.DocumentFunction(function);
                return true;
            }
            return false;
        }

        public override bool VisitVariableDecl(Variable variable)
        {
            // HACK: it doesn't work to call the base as everywhere else because the type of the variable is visited too
            if (this.AlreadyVisited(variable) || variable.TranslationUnit.IsSystemHeader)
            {
                return false;
            }
            base.VisitVariableDecl(variable);
            if (variable.IsGenerated)
            {
                this.documentation.DocumentVariable(variable);
                return true;
            }
            return false;
        }

        private void DocumentFunction(Function function)
        {
            if (function.Comment == null)
            {
                if (function.IsSynthetized)
                {
                    if (function.SynthKind == FunctionSynthKind.DefaultValueOverload)
                    {
                        function.Comment = function.OriginalFunction.Comment;
                    }
                }
                else
                {
                    if (function.Parameters.Any(p => p.Kind == ParameterKind.Extension))
                    {
                        Function instantiatedFrom = function.OriginalFunction.InstantiatedFrom;
                        if (function.OriginalFunction.Comment == null && instantiatedFrom != null)
                        {
                            this.DocumentFunction(instantiatedFrom);
                            function.Comment = instantiatedFrom.Comment;
                            List<Parameter> regularParameters = (from p in function.Parameters
                                                                 where p.Kind == ParameterKind.Regular
                                                                 select p).ToList();
                            List<Parameter> originalRegularParameters = (from p in instantiatedFrom.Parameters
                                                                         where p.Kind == ParameterKind.Regular
                                                                         select p).ToList();
                            for (int i = 0; i < regularParameters.Count; i++)
                            {
                                regularParameters[i].Name = originalRegularParameters[i].Name;
                            }
                        }
                    }
                    else
                    {
                        this.documentation.DocumentFunction(function);
                    }
                }
                if (function.Comment != null)
                {
                    functionsComments[function.Mangled] = function.Comment.BriefText;
                }
            }
        }

        private static readonly Dictionary<string, string> functionsComments = new Dictionary<string, string>();

        private readonly Documentation documentation;
    }
}

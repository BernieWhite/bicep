// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Bicep.Core.Diagnostics;
using Bicep.Core.Extensions;
using Bicep.Core.Features;
using Bicep.Core.Registry;
using Bicep.Core.Semantics.Metadata;
using Bicep.Core.Semantics.Namespaces;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Providers;
using Bicep.Core.TypeSystem.Types;
using Bicep.Core.Utils;
using Bicep.Core.Workspaces;

namespace Bicep.Core.Semantics
{
    public sealed class DeclarationVisitor : AstVisitor
    {
        private readonly INamespaceProvider namespaceProvider;
        private readonly IFeatureProvider features;
        private readonly IArtifactFileLookup artifactFileLookup;
        private readonly ISemanticModelLookup modelLookup;
        private readonly ResourceScope targetScope;
        private readonly ISymbolContext context;
        private readonly BicepSourceFileKind sourceFileKind;
        private readonly IList<ScopeInfo> localScopes;

        private readonly Stack<ScopeInfo> activeScopes = new();

        private DeclarationVisitor(INamespaceProvider namespaceProvider, IFeatureProvider features, IArtifactFileLookup sourceFileLookup, ISemanticModelLookup modelLookup, ResourceScope targetScope, ISymbolContext context, IList<ScopeInfo> localScopes, BicepSourceFileKind sourceFileKind, IArtifactReferenceFactory factory)
        {
            this.namespaceProvider = namespaceProvider;
            this.features = features;
            this.artifactFileLookup = sourceFileLookup;
            this.modelLookup = modelLookup;
            this.targetScope = targetScope;
            this.context = context;
            this.localScopes = localScopes;
            this.sourceFileKind = sourceFileKind;
        }

        // Returns the list of top level declarations as well as top level scopes.
        public static LocalScope GetDeclarations(INamespaceProvider namespaceProvider, IFeatureProvider features, IArtifactFileLookup sourceFileLookup, ISemanticModelLookup modelLookup, ResourceScope targetScope, BicepSourceFile sourceFile, ISymbolContext symbolContext, IArtifactReferenceFactory factory)
        {
            // collect declarations
            var localScopes = new List<ScopeInfo>();
            var declarationVisitor = new DeclarationVisitor(namespaceProvider, features, sourceFileLookup, modelLookup, targetScope, symbolContext, localScopes, sourceFile.FileKind, factory);
            declarationVisitor.Visit(sourceFile.ProgramSyntax);

            return MakeImmutable(localScopes.Single());
        }

        public override void VisitProgramSyntax(ProgramSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.GlobalsOnly);
            this.PushScope(scope);

            base.VisitProgramSyntax(syntax);

            this.PopScope();
        }

        public override void VisitMetadataDeclarationSyntax(MetadataDeclarationSyntax syntax)
        {
            base.VisitMetadataDeclarationSyntax(syntax);

            var symbol = new MetadataSymbol(this.context, syntax.Name.IdentifierName, syntax, syntax.Value);
            DeclareSymbol(symbol);
        }

        public override void VisitParameterDeclarationSyntax(ParameterDeclarationSyntax syntax)
        {
            base.VisitParameterDeclarationSyntax(syntax);

            var symbol = new ParameterSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitTypeDeclarationSyntax(TypeDeclarationSyntax syntax)
        {
            base.VisitTypeDeclarationSyntax(syntax);

            var symbol = new TypeAliasSymbol(this.context, syntax.Name.IdentifierName, syntax, syntax.Value);
            DeclareSymbol(symbol);
        }

        public override void VisitVariableDeclarationSyntax(VariableDeclarationSyntax syntax)
        {
            base.VisitVariableDeclarationSyntax(syntax);

            var symbol = new VariableSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitFunctionDeclarationSyntax(FunctionDeclarationSyntax syntax)
        {
            base.VisitFunctionDeclarationSyntax(syntax);

            var symbol = new DeclaredFunctionSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitResourceDeclarationSyntax(ResourceDeclarationSyntax syntax)
        {
            // Create a scope for each resource body - this ensures that nested resources
            // are contained within the appropriate scope.
            //
            // There may be additional scopes nested inside this between the resource declaration
            // and the actual object body (for-loop). That's OK, in that case, this scope will
            // be empty and we'll use the `for` scope for lookups.
            var bindingSyntax = syntax.Value is IfConditionSyntax ifConditionSyntax ? ifConditionSyntax.Body : syntax.Value;
            var scope = new LocalScope(string.Empty, syntax, bindingSyntax, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritAll);
            this.PushScope(scope);

            base.VisitResourceDeclarationSyntax(syntax);

            this.PopScope();

            // The resource itself should be declared in the enclosing scope - it's accessible to nested
            // resource, but also siblings.
            var symbol = new ResourceSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitModuleDeclarationSyntax(ModuleDeclarationSyntax syntax)
        {
            base.VisitModuleDeclarationSyntax(syntax);

            var symbol = new ModuleSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitTestDeclarationSyntax(TestDeclarationSyntax syntax)
        {
            base.VisitTestDeclarationSyntax(syntax);

            var symbol = new TestSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitOutputDeclarationSyntax(OutputDeclarationSyntax syntax)
        {
            base.VisitOutputDeclarationSyntax(syntax);

            var symbol = new OutputSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitAssertDeclarationSyntax(AssertDeclarationSyntax syntax)
        {
            base.VisitAssertDeclarationSyntax(syntax);

            var symbol = new AssertSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitProviderDeclarationSyntax(ProviderDeclarationSyntax syntax)
        {
            base.VisitProviderDeclarationSyntax(syntax);
            DeclareSymbol(new ProviderNamespaceSymbol(this.context, syntax, ResolveProviderSymbol(syntax)));
        }

        private TypeSymbol ResolveProviderSymbol(ProviderDeclarationSyntax syntax)
        {
            if (!features.ExtensibilityEnabled)
            {
                return ErrorType.Create(DiagnosticBuilder.ForPosition(syntax).ProvidersAreDisabled());
            }

            // Check for interpolated specification strings
            if (syntax.SpecificationString is StringSyntax specificationString && specificationString.IsInterpolated())
            {
                return ErrorType.Create(DiagnosticBuilder.ForPosition(syntax.SpecificationString).ProviderSpecificationInterpolationUnsupported());
            }

            if (!syntax.Specification.IsValid)
            {
                return (syntax.SpecificationString is StringSyntax)
                    ? ErrorType.Create(DiagnosticBuilder.ForPosition(syntax.SpecificationString).InvalidProviderSpecification())
                    : ErrorType.Empty();
            }

            // Check if the MSGraph provider is recognized and enabled
            if (syntax.Specification.Name == MicrosoftGraphNamespaceType.BuiltInName && !features.MicrosoftGraphPreviewEnabled)
            {
                return ErrorType.Create(DiagnosticBuilder.ForPosition(syntax).UnrecognizedProvider(syntax.Specification.Name));
            }

            // Check if the Az provider is recognized and enabled
            if (syntax.Specification.Name == AzNamespaceType.BuiltInName && !features.DynamicTypeLoadingEnabled)
            {
                return ErrorType.Create(DiagnosticBuilder.ForPosition(syntax).UnrecognizedProvider(syntax.Specification.Name));
            }

            Uri? typesBaseUri = null;
            if (syntax.Path is not null &&
                !this.artifactFileLookup.TryGetResourceTypesFileUri(syntax).IsSuccess(out typesBaseUri, out var errorBuilder))
            {
                return ErrorType.Create(errorBuilder(DiagnosticBuilder.ForPosition(syntax)));
            }

            ResourceTypesProviderDescriptor providerDescriptor = new(
                syntax.Specification.Name,
                syntax.Specification.Version,
                isImplicitImport: false,
                syntax.Alias?.IdentifierName,
                syntax.Specification.BicepRegistryAddress,
                typesBaseUri);

            if (!namespaceProvider.TryGetNamespace(providerDescriptor, targetScope, features, sourceFileKind).IsSuccess(out var namespaceType, out errorBuilder))
            {
                return ErrorType.Create(errorBuilder(DiagnosticBuilder.ForPosition(syntax)));
            }

            return namespaceType;
        }

        public override void VisitParameterAssignmentSyntax(ParameterAssignmentSyntax syntax)
        {
            base.VisitParameterAssignmentSyntax(syntax);

            var symbol = new ParameterAssignmentSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitLambdaSyntax(LambdaSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritAll);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
            */
            foreach (var variable in syntax.GetLocalVariables())
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, variable.Name.IdentifierName, variable, LocalKind.LambdaItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            // visit the children
            base.VisitLambdaSyntax(syntax);

            this.PopScope();
        }

        public override void VisitTypedLambdaSyntax(TypedLambdaSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritFunctionsOnly);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
            */
            foreach (var variable in syntax.GetLocalVariables())
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, variable.Name.IdentifierName, variable, LocalKind.LambdaItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            // visit the children
            base.VisitTypedLambdaSyntax(syntax);

            this.PopScope();
        }

        public override void VisitForSyntax(ForSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritAll);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
             */
            var itemVariable = syntax.ItemVariable;
            if (itemVariable is not null)
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, itemVariable.Name.IdentifierName, itemVariable, LocalKind.ForExpressionItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            var indexVariable = syntax.IndexVariable;
            if (indexVariable is not null)
            {
                var indexVariableSymbol = new LocalVariableSymbol(this.context, indexVariable.Name.IdentifierName, indexVariable, LocalKind.ForExpressionIndexVariable);
                DeclareSymbol(indexVariableSymbol);
            }

            // visit the children
            base.VisitForSyntax(syntax);

            this.PopScope();
        }

        public override void VisitCompileTimeImportDeclarationSyntax(CompileTimeImportDeclarationSyntax syntax)
        {
            base.VisitCompileTimeImportDeclarationSyntax(syntax);

            if (GetImportSourceModel(syntax).IsSuccess(out var model, out var modelLoadError))
            {
                switch (syntax.ImportExpression)
                {
                    case WildcardImportSyntax wildcardImport:
                        DeclareSymbol(new WildcardImportSymbol(context, model, wildcardImport, syntax));
                        break;
                    case ImportedSymbolsListSyntax importedSymbolsList:
                        foreach (var item in importedSymbolsList.ImportedSymbols)
                        {
                            if (item.TryGetOriginalSymbolNameText() is not string importedOriginalName)
                            {
                                // If the imported symbol's name cannot be determined, an error will be surfaced by the parser
                                continue;
                            }

                            DeclareSymbol(model.Exports.TryGetValue(importedOriginalName, out var exportMetadata) switch
                            {
                                true => exportMetadata switch
                                {
                                    ExportedTypeMetadata exportedType => new ImportedTypeSymbol(context, item, syntax, model, exportedType),
                                    ExportedVariableMetadata exportedVariable => new ImportedVariableSymbol(context, item, syntax, model, exportedVariable),
                                    ExportedFunctionMetadata exportedFunction when features.UserDefinedFunctionsEnabled => new ImportedFunctionSymbol(context, item, syntax, model, exportedFunction),
                                    ExportedFunctionMetadata => new ErroredImportSymbol(context,
                                        importedOriginalName,
                                        item,
                                        item.Name,
                                        ImmutableArray.Create(DiagnosticBuilder.ForPosition(item.OriginalSymbolName).FuncDeclarationStatementsUnsupported())),
                                    _ when exportMetadata.Kind == ExportMetadataKind.Error => new ErroredImportSymbol(context,
                                        importedOriginalName,
                                        item,
                                        item.Name,
                                        ImmutableArray.Create(DiagnosticBuilder.ForPosition(item.OriginalSymbolName).ImportedSymbolHasErrors(importedOriginalName, exportMetadata.Description ?? "unknown error"))),
                                    _ => new ErroredImportSymbol(context,
                                        importedOriginalName,
                                        item,
                                        item.Name,
                                        ImmutableArray.Create(DiagnosticBuilder.ForPosition(item.OriginalSymbolName).ImportedSymbolHasErrors(importedOriginalName, $"Unsupported export kind: {exportMetadata.Kind}"))),
                                },
                                false => new ErroredImportSymbol(context,
                                    importedOriginalName,
                                    item,
                                    item.Name,
                                    ImmutableArray.Create(DiagnosticBuilder.ForPosition(item.OriginalSymbolName).ImportedSymbolNotFound(importedOriginalName))),
                            });
                        }
                        break;
                }
            }
            else
            {
                switch (syntax.ImportExpression)
                {
                    case WildcardImportSyntax wildcardImport:
                        DeclareSymbol(new ErroredImportSymbol(context, wildcardImport.Name.IdentifierName, wildcardImport, wildcardImport.Name, ImmutableArray.Create(modelLoadError)));
                        break;
                    case ImportedSymbolsListSyntax importedSymbolsList:
                        var loadErrorRecorded = false;
                        foreach (var item in importedSymbolsList.ImportedSymbols)
                        {
                            if (item.TryGetOriginalSymbolNameText() is not string importedOriginalName)
                            {
                                // If the imported symbol's name cannot be determined, an error will be surfaced by the parser
                                continue;
                            }

                            // only include the load error once per import statement
                            var errors = loadErrorRecorded ? ImmutableArray<ErrorDiagnostic>.Empty : ImmutableArray.Create(modelLoadError);
                            DeclareSymbol(new ErroredImportSymbol(context, importedOriginalName, item, item.Name, errors));
                        }
                        break;
                }
            }
        }

        private Result<ISemanticModel, ErrorDiagnostic> GetImportSourceModel(CompileTimeImportDeclarationSyntax syntax)
        {
            if (!features.CompileTimeImportsEnabled)
            {
                return new(DiagnosticBuilder.ForPosition(syntax).CompileTimeImportsNotSupported());
            }

            if (!SemanticModelHelper.TryGetModelForArtifactReference(artifactFileLookup, syntax, modelLookup).IsSuccess(out var model, out var modelLoadError))
            {
                return new(modelLoadError);
            }

            if (model.HasErrors())
            {
                return new(model is ArmTemplateSemanticModel
                    ? DiagnosticBuilder.ForPosition(syntax.FromClause).ReferencedArmTemplateHasErrors()
                    : DiagnosticBuilder.ForPosition(syntax.FromClause).ReferencedModuleHasErrors());
            }

            return new(model);
        }

        private void DeclareSymbol(DeclaredSymbol symbol)
        {
            if (this.activeScopes.TryPeek(out var current))
            {
                current.Locals.Add(symbol);
            }
        }

        private void PushScope(LocalScope scope)
        {
            var item = new ScopeInfo(scope);

            if (this.activeScopes.TryPeek(out var current))
            {
                if (object.ReferenceEquals(current.Scope.BindingSyntax, scope.BindingSyntax))
                {
                    throw new InvalidOperationException($"Attempting to redefine the scope for {current.Scope.BindingSyntax}");
                }

                // add this one to the parent
                current.Children.Add(item);
            }
            else
            {
                // add this to the root list
                this.localScopes.Add(item);
            }

            this.activeScopes.Push(item);
        }

        private void PopScope()
        {
            this.activeScopes.Pop();
        }

        private static LocalScope MakeImmutable(ScopeInfo info)
        {
            return info.Scope.ReplaceChildren(info.Children.Select(MakeImmutable)).ReplaceLocals(info.Locals);
        }

        /// <summary>
        /// Allows us to mutate child scopes without having to swap out items on the stack
        /// which is fragile.
        /// </summary>
        /// <remarks>This could be replaced with a record if we could target .net 5</remarks>
        private class ScopeInfo
        {
            public ScopeInfo(LocalScope scope)
            {
                this.Scope = scope;
            }

            public LocalScope Scope { get; }

            public IList<DeclaredSymbol> Locals { get; } = new List<DeclaredSymbol>();

            public IList<ScopeInfo> Children { get; } = new List<ScopeInfo>();
        }
    }
}

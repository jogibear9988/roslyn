﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.Host.Mef
Imports Microsoft.CodeAnalysis.LanguageServices
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.Providers
    <ExportCompletionProvider(NameOf(EnumCompletionProvider), LanguageNames.VisualBasic)>
    <ExtensionOrder(After:=NameOf(ObjectCreationCompletionProvider))>
    <[Shared]>
    Partial Friend Class EnumCompletionProvider
        Inherits AbstractSymbolCompletionProvider(Of VisualBasicSyntaxContext)

        <ImportingConstructor>
        <Obsolete(MefConstruction.ImportingConstructorMessage, True)>
        Public Sub New()
        End Sub

        Protected Overrides Function GetPreselectedSymbolsAsync(
                context As VisualBasicSyntaxContext, position As Integer, options As OptionSet, cancellationToken As CancellationToken) As Task(Of ImmutableArray(Of ISymbol))

            If context.SyntaxTree.IsInNonUserCode(context.Position, cancellationToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            ' This providers provides fully qualified names, eg "DayOfWeek.Monday"
            ' Don't run after dot because SymbolCompletionProvider will provide
            ' members in situations like Dim x = DayOfWeek.$$
            If context.TargetToken.IsKind(SyntaxKind.DotToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim typeInferenceService = context.GetLanguageService(Of ITypeInferenceService)()
            Dim enumType = typeInferenceService.InferType(context.SemanticModel, position, objectAsDefault:=True, cancellationToken:=cancellationToken)

            If enumType.TypeKind <> TypeKind.Enum Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim hideAdvancedMembers = options.GetOption(CodeAnalysis.Recommendations.RecommendationOptions.HideAdvancedMembers, context.SemanticModel.Language)

            ' We'll want to build a list of the actual enum members and all accessible instances of that enum, too
            Dim result = enumType.GetMembers().Where(
                Function(m As ISymbol) As Boolean
                    Return m.Kind = SymbolKind.Field AndAlso
                        DirectCast(m, IFieldSymbol).IsConst AndAlso
                        m.IsEditorBrowsable(hideAdvancedMembers, context.SemanticModel.Compilation)
                End Function).ToImmutableArray()

            Return Task.FromResult(result)
        End Function

        Protected Overrides Function GetSymbolsAsync(
                context As VisualBasicSyntaxContext, position As Integer, options As OptionSet, cancellationToken As CancellationToken) As Task(Of ImmutableArray(Of ISymbol))

            If context.SyntaxTree.IsInNonUserCode(context.Position, cancellationToken) OrElse
                context.SyntaxTree.IsInSkippedText(position, cancellationToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            If context.TargetToken.IsKind(SyntaxKind.DotToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim typeInferenceService = context.GetLanguageService(Of ITypeInferenceService)()
            Dim span = New TextSpan(position, 0)
            Dim enumType = typeInferenceService.InferType(context.SemanticModel, position, objectAsDefault:=True, cancellationToken:=cancellationToken)

            If enumType.TypeKind <> TypeKind.Enum Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim hideAdvancedMembers = options.GetOption(CodeAnalysis.Recommendations.RecommendationOptions.HideAdvancedMembers, context.SemanticModel.Language)

            Dim otherSymbols = context.SemanticModel.LookupSymbols(position).WhereAsArray(
                Function(s) s.MatchesKind(SymbolKind.Field, SymbolKind.Local, SymbolKind.Parameter, SymbolKind.Property) AndAlso
                    s.IsEditorBrowsable(hideAdvancedMembers, context.SemanticModel.Compilation))

            Dim otherInstances = otherSymbols.WhereAsArray(Function(s) Equals(enumType, GetTypeFromSymbol(s)))

            Return Task.FromResult(otherInstances.Concat(enumType))
        End Function

        Friend Overrides Function IsInsertionTrigger(text As SourceText, characterPosition As Integer, options As OptionSet) As Boolean
            Return text(characterPosition) = " "c OrElse
                text(characterPosition) = "("c OrElse
                (characterPosition > 1 AndAlso text(characterPosition) = "="c AndAlso text(characterPosition - 1) = ":"c) OrElse
                SyntaxFacts.IsIdentifierStartCharacter(text(characterPosition)) AndAlso
                options.GetOption(CompletionOptions.TriggerOnTypingLetters2, LanguageNames.VisualBasic)
        End Function

        Friend Overrides ReadOnly Property TriggerCharacters As ImmutableHashSet(Of Char) = ImmutableHashSet.Create(" "c, "("c, "="c)

        Private Shared Function GetTypeFromSymbol(symbol As ISymbol) As ITypeSymbol
            Dim symbolType = If(TryCast(symbol, IFieldSymbol)?.Type,
                             If(TryCast(symbol, ILocalSymbol)?.Type,
                             If(TryCast(symbol, IParameterSymbol)?.Type,
                                TryCast(symbol, IPropertySymbol)?.Type)))
            Return symbolType
        End Function

        ' PERF: Cached values for GetDisplayAndInsertionText. Cuts down on the number of calls to ToMinimalDisplayString for large enums.
        Private _cachedDisplayAndInsertionTextContainingType As INamedTypeSymbol
        Private _cachedDisplayAndInsertionTextContext As VisualBasicSyntaxContext
        Private _cachedDisplayAndInsertionTextContainingTypeText As String

        Protected Overrides Function GetDisplayAndSuffixAndInsertionText(symbol As ISymbol, context As VisualBasicSyntaxContext) As (displayText As String, suffix As String, insertionText As String)
            If symbol.ContainingType IsNot Nothing AndAlso symbol.ContainingType.TypeKind = TypeKind.Enum Then
                If Not Equals(_cachedDisplayAndInsertionTextContainingType, symbol.ContainingType) OrElse _cachedDisplayAndInsertionTextContext IsNot context Then
                    Dim displayFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType).WithLocalOptions(SymbolDisplayLocalOptions.None)
                    Dim displayService = context.GetLanguageService(Of ISymbolDisplayService)()
                    _cachedDisplayAndInsertionTextContainingTypeText = symbol.ContainingType.ToMinimalDisplayString(context.SemanticModel, context.Position, displayFormat)
                    _cachedDisplayAndInsertionTextContainingType = symbol.ContainingType
                    _cachedDisplayAndInsertionTextContext = context
                End If

                Dim text As String = _cachedDisplayAndInsertionTextContainingTypeText & "." & symbol.Name
                Return (text, "", text)
            End If

            Return CompletionUtilities.GetDisplayAndSuffixAndInsertionText(symbol, context)
        End Function

        Protected Overrides Function CreateItem(completionContext As CompletionContext,
                displayText As String, displayTextSuffix As String, insertionText As String,
                symbols As List(Of ISymbol), context As VisualBasicSyntaxContext, preselect As Boolean, supportedPlatformData As SupportedPlatformData) As CompletionItem
            Dim rules = GetCompletionItemRules(symbols)
            rules = rules.WithMatchPriority(If(preselect, MatchPriority.Preselect, MatchPriority.Default))

            Return SymbolCompletionItem.CreateWithSymbolId(
                displayText:=displayText,
                displayTextSuffix:=displayTextSuffix,
                insertionText:=insertionText,
                filterText:=GetFilterText(symbols(0), displayText, context),
                symbols:=symbols,
                contextPosition:=context.Position,
                sortText:=insertionText,
                supportedPlatforms:=supportedPlatformData,
                rules:=rules)
        End Function

        Private Shared ReadOnly s_rules As CompletionItemRules =
            CompletionItemRules.Default.WithMatchPriority(MatchPriority.Preselect)

        Protected Overrides Function GetCompletionItemRules(symbols As IReadOnlyList(Of ISymbol)) As CompletionItemRules
            Return s_rules
        End Function

        Public Overrides Function GetTextChangeAsync(document As Document, selectedItem As CompletionItem, ch As Char?, cancellationToken As CancellationToken) As Task(Of TextChange?)
            Dim insertionText As String = SymbolCompletionItem.GetInsertionText(selectedItem)
            Return Task.FromResult(Of TextChange?)(New TextChange(selectedItem.Span, insertionText))
        End Function

        Protected Overrides Function GetInsertionText(item As CompletionItem, ch As Char) As String
            Return CompletionUtilities.GetInsertionTextAtInsertionTime(item, ch)
        End Function
    End Class
End Namespace

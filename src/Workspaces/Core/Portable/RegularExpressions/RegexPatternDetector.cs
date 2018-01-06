﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.VirtualChars;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.RegularExpressions
{
    /// <summary>
    /// Helper class to detect regex pattern tokens in a document efficiently.
    /// </summary>
    internal class RegexPatternDetector
    {
        private const string _patternName = "pattern";

        private static readonly ConditionalWeakTable<SemanticModel, RegexPatternDetector> _modelToDetector =
            new ConditionalWeakTable<SemanticModel, RegexPatternDetector>();

        private readonly SemanticModel _semanticModel;
        private readonly ISyntaxFactsService _syntaxFacts;
        private readonly ISemanticFactsService _semanticFacts;
        private readonly INamedTypeSymbol _regexType;
        private readonly HashSet<string> _methodNamesOfInterest;

        /// <summary>
        /// Helps match patterns of the form: language=regex,option1,option2,option3
        /// 
        /// All matching is case insensitive, with spaces allowed between the punctuation.
        /// 'regex' or 'regexp' are both allowed.  Option values will be or'ed together
        /// to produce final options value.  If an unknown option is encountered, processing
        /// will stop with whatever value has accumulated so far.
        /// 
        /// Option names are the values from the <see cref="RegexOptions"/> enum.
        /// </summary>
        private static readonly Regex s_languageCommentDetector = 
            new Regex(@"language\s*=\s*regex(p)?((\s*,\s*)(?<option>[a-zA-Z]+))*",
                RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, RegexOptions> s_nameToOption =
            typeof(RegexOptions).GetTypeInfo().DeclaredFields
                .Where(f => f.FieldType == typeof(RegexOptions))
                .ToDictionary(f => f.Name, f => (RegexOptions)f.GetValue(null), StringComparer.OrdinalIgnoreCase);

        public RegexPatternDetector(
            SemanticModel semanticModel, 
            ISyntaxFactsService syntaxFacts,
            ISemanticFactsService semanticFacts,
            INamedTypeSymbol regexType, 
            HashSet<string> methodNamesOfInterest)
        {
            _semanticModel = semanticModel;
            _syntaxFacts = syntaxFacts;
            _semanticFacts = semanticFacts;
            _regexType = regexType;
            _methodNamesOfInterest = methodNamesOfInterest;
        }

        public static RegexPatternDetector TryGetOrCreate(
            SemanticModel semanticModel,
            ISyntaxFactsService syntaxFacts,
            ISemanticFactsService semanticFacts)
        {
            // Do a quick non-allocating check first.
            if (_modelToDetector.TryGetValue(semanticModel, out var detector))
            {
                return detector;
            }

            return _modelToDetector.GetValue(
                semanticModel, _ => TryCreate(semanticModel, syntaxFacts, semanticFacts));
        }

        private static RegexPatternDetector TryCreate(
            SemanticModel semanticModel, 
            ISyntaxFactsService syntaxFacts, 
            ISemanticFactsService semanticFacts)
        {
            var regexType = semanticModel.Compilation.GetTypeByMetadataName(typeof(Regex).FullName);
            if (regexType == null)
            {
                return null;
            }

            var methodNamesOfInterest = GetMethodNamesOfInterest(regexType, syntaxFacts);
            return new RegexPatternDetector(
                semanticModel, syntaxFacts, semanticFacts,
                regexType, methodNamesOfInterest);
        }

        public static bool IsDefinitelyNotPattern(SyntaxToken token, ISyntaxFactsService syntaxFacts)
        {
            // We only support string literals passed in arguments to something.
            // In the future we could support any string literal, as long as it has
            // some marker (like a comment on it) stating it's a regex.
            if (!syntaxFacts.IsStringLiteral(token))
            {
                return true;
            }

            if (!IsMethodOrConstructorArgument(token, syntaxFacts) && 
                !HasRegexLanguageComment(token, syntaxFacts, out _))
            {
                return true;
            }

            return false;
        }

        private static bool HasRegexLanguageComment(
            SyntaxToken token, ISyntaxFactsService syntaxFacts, out RegexOptions options)
        {
            if (HasRegexLanguageComment(token.GetPreviousToken().TrailingTrivia, syntaxFacts, out options))
            {
                return true;
            }

            for (var node = token.Parent; node != null; node = node.Parent)
            {
                if (HasRegexLanguageComment(node.GetLeadingTrivia(), syntaxFacts, out options))
                {
                    return true;
                }
            }

            options = default;
            return false;
        }

        private static bool HasRegexLanguageComment(
            SyntaxTriviaList list, ISyntaxFactsService syntaxFacts, out RegexOptions options)
        {
            foreach (var trivia in list)
            {
                if (HasRegexLanguageComment(trivia, syntaxFacts, out options))
                {
                    return true;
                }
            }

            options = default;
            return false;
        }

        private static bool HasRegexLanguageComment(
            SyntaxTrivia trivia, ISyntaxFactsService syntaxFacts, out RegexOptions options)
        {
            if (syntaxFacts.IsRegularComment(trivia))
            {
                var text = trivia.ToString();
                var match = s_languageCommentDetector.Match(text);
                if (match.Success)
                {
                    options = RegexOptions.None;

                    var optionGroup = match.Groups["option"];
                    foreach (Capture capture in optionGroup.Captures)
                    {
                        if (s_nameToOption.TryGetValue(capture.Value, out var specificOption))
                        {
                            options |= specificOption;
                        }
                        else
                        {
                            break;
                        }
                    }

                    return true;
                }
            }

            options = default;
            return false;
        }

        private static bool IsMethodOrConstructorArgument(SyntaxToken token, ISyntaxFactsService syntaxFacts)
            => syntaxFacts.IsLiteralExpression(token.Parent) &&
               syntaxFacts.IsArgument(token.Parent.Parent);

        private static HashSet<string> GetMethodNamesOfInterest(INamedTypeSymbol regexType, ISyntaxFactsService syntaxFacts)
        {
            var result = syntaxFacts.IsCaseSensitive
                ? new HashSet<string>()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var methods = from method in regexType.GetMembers().OfType<IMethodSymbol>()
                          where method.DeclaredAccessibility == Accessibility.Public
                          where method.IsStatic
                          where method.Parameters.Any(p => p.Name == _patternName)
                          select method.Name;

            result.AddRange(methods);

            return result;
        }

        public bool IsRegexPattern(SyntaxToken token, CancellationToken cancellationToken, out RegexOptions options)
        {
            options = default;
            if (IsDefinitelyNotPattern(token, _syntaxFacts))
            {
                return false;
            }

            if (HasRegexLanguageComment(token, _syntaxFacts, out options))
            {
                return true;
            }

            var stringLiteral = token;
            var literalNode = stringLiteral.Parent;
            var argumentNode = literalNode.Parent;
            Debug.Assert(_syntaxFacts.IsArgument(argumentNode));

            var argumentList = argumentNode.Parent;
            var invocationOrCreation = argumentList.Parent;
            if (_syntaxFacts.IsInvocationExpression(invocationOrCreation))
            {
                var invokedExpression = _syntaxFacts.GetExpressionOfInvocationExpression(invocationOrCreation);
                var name = GetNameOfInvokedExpression(invokedExpression);
                if (_methodNamesOfInterest.Contains(name))
                {
                    // Is a string argument to a method that looks like it could be a Regex method.  
                    // Need to do deeper analysis
                    var method = _semanticModel.GetSymbolInfo(invocationOrCreation, cancellationToken).GetAnySymbol();
                    if (method != null &&
                        method.DeclaredAccessibility == Accessibility.Public &&
                        method.IsStatic &&
                        _regexType.Equals(method.ContainingType))
                    {
                        return AnalyzeStringLiteral(
                            stringLiteral, argumentNode, cancellationToken, out options);
                    }
                }
            }
            else if (_syntaxFacts.IsObjectCreationExpression(invocationOrCreation))
            {
                var typeNode = _syntaxFacts.GetObjectCreationType(invocationOrCreation);
                var name = GetNameOfType(typeNode, _syntaxFacts);
                if (name != null)
                {
                    if (_syntaxFacts.StringComparer.Compare(nameof(Regex), name) == 0)
                    {
                        var constructor = _semanticModel.GetSymbolInfo(invocationOrCreation, cancellationToken).GetAnySymbol();
                        if (_regexType.Equals(constructor?.ContainingType))
                        {
                            // Argument to "new Regex".  Need to do deeper analysis
                            return AnalyzeStringLiteral(
                                stringLiteral, argumentNode, cancellationToken, out options);
                        }
                    }
                }
            }

            return false;
        }

        public RegexTree TryParseRegexPattern(SyntaxToken token, IVirtualCharService virtualCharService, CancellationToken cancellationToken)
        {
            if (!this.IsRegexPattern(token, cancellationToken, out var options))
            {
                return null;
            }

            var chars = virtualCharService.TryConvertToVirtualChars(token);
            if (chars.IsDefaultOrEmpty)
            {
                return null;
            }

            return RegexParser.TryParse(chars, options);
        }

        private bool AnalyzeStringLiteral(
            SyntaxToken stringLiteral, SyntaxNode argumentNode, 
            CancellationToken cancellationToken, out RegexOptions options)
        {
            options = default;

            var parameter = _semanticFacts.FindParameterForArgument(_semanticModel, argumentNode, cancellationToken);
            if (parameter?.Name != _patternName)
            {
                return false;
            }

            options = GetRegexOptions(argumentNode, cancellationToken);
            return true;
        }

        private RegexOptions GetRegexOptions(SyntaxNode argumentNode, CancellationToken cancellationToken)
        {
            var argumentList = argumentNode.Parent;
            var arguments = _syntaxFacts.GetArgumentsOfArgumentList(argumentList);
            foreach (var siblingArg in arguments)
            {
                if (siblingArg != argumentNode)
                {
                    var expr = _syntaxFacts.GetExpressionOfArgument(siblingArg);
                    if (expr != null)
                    {
                        var exprType = _semanticModel.GetTypeInfo(expr, cancellationToken);
                        if (exprType.Type?.Name == nameof(RegexOptions))
                        {
                            var constVal = _semanticModel.GetConstantValue(expr, cancellationToken);
                            if (constVal.HasValue)
                            {
                                return (RegexOptions)(int)constVal.Value;
                            }
                        }
                    }
                }
            }

            return RegexOptions.None;
        }

        private string GetNameOfType(SyntaxNode typeNode, ISyntaxFactsService syntaxFacts)
        {
            if (syntaxFacts.IsQualifiedName(typeNode))
            {
                return GetNameOfType(syntaxFacts.GetRightSideOfDot(typeNode), syntaxFacts);
            }
            else if (syntaxFacts.IsIdentifierName(typeNode))
            {
                return syntaxFacts.GetIdentifierOfSimpleName(typeNode).ValueText;
            }

            return null;
        }

        private string GetNameOfInvokedExpression(SyntaxNode invokedExpression)
        {
            if (_syntaxFacts.IsSimpleMemberAccessExpression(invokedExpression))
            {
                return _syntaxFacts.GetIdentifierOfSimpleName(_syntaxFacts.GetNameOfMemberAccessExpression(invokedExpression)).ValueText;
            }
            else if (_syntaxFacts.IsIdentifierName(invokedExpression))
            {
                return _syntaxFacts.GetIdentifierOfSimpleName(invokedExpression).ValueText;
            }

            return null;
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CS = Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.CodeConverter.CSharp
{
    public class TriviaConverter
    {
        private static readonly string TrailingTriviaConversionKind = $"{nameof(TriviaConverter)}.TrailingTriviaConversion.Id";

        /// <summary>
        /// The source of truth for a source node's conversion id. This dictates that the source token's trailing trivia will be converted and placed on the node with that conversion id.
        /// </summary>
        private readonly Dictionary<SyntaxToken, string> trailingTriviaConversionsBySource = new Dictionary<SyntaxToken, string>();

        /// <summary>
        /// Because annotation data can only be a string, use a dictionary to store the information actually desired.
        /// Note, this is NOT just the inverse of <see cref="trailingTriviaConversionsBySource"/>.
        /// Crucially, the source token contained here is the one originally intended, and may now be obsolete.
        /// </summary>
        private readonly Dictionary<string, SyntaxToken> annotationData = new Dictionary<string, SyntaxToken>();

        public T PortConvertedTrivia<T>(SyntaxNode sourceNode, T destination) where T : SyntaxNode
        {
            if (destination == null || sourceNode == null) return destination;

            destination = sourceNode.HasLeadingTrivia
                ? destination.WithLeadingTrivia(sourceNode.GetLeadingTrivia().ConvertTrivia())
                : destination;
            
            if (sourceNode.HasTrailingTrivia) {
                var lastDestToken = destination.GetLastToken();
                destination = destination.ReplaceToken(lastDestToken, WithDelegateToParentAnnotation(sourceNode, lastDestToken));
            }

            if (!(destination is CS.Syntax.CompilationUnitSyntax)) return destination;
            
            return WithTrailingTriviaConversions(destination, sourceNode.Parent?.GetLastToken(), true);
                
        }

        private SyntaxToken MoveChildTrailingEndOfLinesToToken<T>(T destination, SyntaxToken beforeOpenBraceToken)
            where T : SyntaxNode
        {
            var conversionAnnotations = destination.GetAnnotatedTokens(TrailingTriviaConversionKind)
                .TakeWhile(t => t.FullSpan.Start < beforeOpenBraceToken.FullSpan.Start)
                .SelectMany(t => t.GetAnnotations(TrailingTriviaConversionKind).ToList())
                .ToList();
            foreach (var conversionAnnotation in conversionAnnotations) {
                var conversionId = conversionAnnotation.Data;
                var sourceSyntaxToken = annotationData[conversionId];

                if (trailingTriviaConversionsBySource.TryGetValue(sourceSyntaxToken, out var latestReplacementId) &&
                    latestReplacementId == conversionId
                    && sourceSyntaxToken.TrailingTrivia.Any(t => t.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.EndOfLineTrivia))) {
                    beforeOpenBraceToken = WithDelegateToParentAnnotation(sourceSyntaxToken, beforeOpenBraceToken);
                }
            }
            return beforeOpenBraceToken;
        }

        /// <summary>
        /// Trivia is attached to tokens, only port it when we're at the highest level for which it's the trailing trivia in the source
        /// </summary>
        /// <remarks>
        /// Because of differences in structure between C# and VB:
        ///  1) Trivia will be ported to the wrong place in a line, e.g. before the semicolon
        ///  2)  Not every node will be visited, and hence Trivia would sometimes be missed
        /// For (1), trailing trivia (often containing newlines) this is particularly problematic, so  we only do a replacement when the
        /// trailing trivia isn't also its parent's trailing trivia.
        /// For (2) the ability to schedule replacements here allows the trivia porting to remain separate from the main transformation
        /// </remarks>
        private T WithTrailingTriviaConversions<T>(T destination, SyntaxToken? parentLastToken, bool hasVisitedContainingBlock) where T : SyntaxNode
        {
            var destinationsWithConversions = destination.GetAnnotatedTokens(TrailingTriviaConversionKind);
            destination = destination.ReplaceTokens(destinationsWithConversions, (originalToken, updatedToken) =>
            {
                foreach (var conversionAnnotation in updatedToken.GetAnnotations(TrailingTriviaConversionKind).ToList()) {
                    var conversionId = conversionAnnotation.Data;
                    var foundAnnotation = annotationData.TryGetValue(conversionId, out var sourceSyntaxToken);
                    if (foundAnnotation && parentLastToken == sourceSyntaxToken
                    || !hasVisitedContainingBlock) {
                        continue;
                    };

                    // Only port trivia if this replacement hasn't been superseded by another 
                    if (foundAnnotation && // BUG: Fix sometimes not finding annotation
                        trailingTriviaConversionsBySource.TryGetValue(sourceSyntaxToken, out var latestReplacementId) &&
                        latestReplacementId == conversionId) {
                        updatedToken = updatedToken.WithConvertedTrailingTriviaFrom(sourceSyntaxToken);
                        trailingTriviaConversionsBySource.Remove(sourceSyntaxToken);
                    }

                    // Remove annotations since it's either done, or obsolete. So we don't have to keep iterating over it for no reason.
                    updatedToken = updatedToken.WithoutAnnotations(conversionAnnotation);
                    annotationData.Remove(conversionId);
                }
                return updatedToken;
            });
            return destination;
        }

        private static bool IsFirstLineOfBlockConstruct(StatementSyntax s)
        {
            return !(s is DeclarationStatementSyntax);
        }

        /// <summary>
        /// Because <paramref name="destination"/> is immutable, any changes (such as gaining a parent) a new version to be created.
        /// Adding an annotation allows tracking this node, since it will stay with it in any reincarnations.
        /// </summary>
        public SyntaxToken WithDelegateToParentAnnotation(SyntaxToken lastSourceToken, SyntaxToken destination)
        {
            var identifier = lastSourceToken.GetHashCode() + "|" + destination.GetHashCode();
            trailingTriviaConversionsBySource[lastSourceToken] = identifier;

            destination = destination.WithAdditionalAnnotations(new SyntaxAnnotation(TrailingTriviaConversionKind, identifier));
            annotationData.Add(identifier, lastSourceToken);
            return destination;
        }

        public SyntaxToken WithDelegateToParentAnnotation(SyntaxNode unvisitedSourceStatement, SyntaxToken destinationToken)
        {
            return unvisitedSourceStatement == null ? destinationToken
                : WithDelegateToParentAnnotation(unvisitedSourceStatement.GetLastToken(), destinationToken);
        }

        public SyntaxToken WithDelegateToParentAnnotation<T>(SyntaxList<T> unvisitedSourceStatementList, SyntaxToken destinationToken) where T: SyntaxNode
        {
            return WithDelegateToParentAnnotation(unvisitedSourceStatementList.LastOrDefault(), destinationToken);
        }

        public bool IsAllTriviaConverted()
        {
            return trailingTriviaConversionsBySource.Any(t => t.Key.TrailingTrivia.Any(x => !x.IsWhitespaceOrEndOfLine()));
        }
    }
}
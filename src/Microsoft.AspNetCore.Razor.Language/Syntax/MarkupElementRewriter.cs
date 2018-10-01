// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Microsoft.AspNetCore.Razor.Language.Syntax
{
    internal class MarkupElementRewriter
    {
        public static RazorSyntaxTree Rewrite(RazorSyntaxTree syntaxTree)
        {
            var rewriter = new Rewriter();
            var rewrittenRoot = rewriter.Visit(syntaxTree.Root);

            var newSyntaxTree = RazorSyntaxTree.Create(rewrittenRoot, syntaxTree.Source, syntaxTree.Diagnostics, syntaxTree.Options);
            return newSyntaxTree;
        }

        public static RazorSyntaxTree RemoveMarkupElement(RazorSyntaxTree syntaxTree)
        {
            var remover = new Remover();
            var rewrittenRoot = remover.Visit(syntaxTree.Root);

            var newSyntaxTree = RazorSyntaxTree.Create(rewrittenRoot, syntaxTree.Source, syntaxTree.Diagnostics, syntaxTree.Options);
            return newSyntaxTree;
        }

        private class Rewriter : SyntaxRewriter
        {
            // From http://dev.w3.org/html5/spec/Overview.html#elements-0
            private static readonly HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "area",
                "base",
                "br",
                "col",
                "command",
                "embed",
                "hr",
                "img",
                "input",
                "keygen",
                "link",
                "meta",
                "param",
                "source",
                "track",
                "wbr"
            };

            private readonly Stack<TagBlockTracker> _startTagTracker;

            public Rewriter()
            {
                _startTagTracker = new Stack<TagBlockTracker>();
            }

            private TagBlockTracker CurrentTracker => _startTagTracker.Count > 0 ? _startTagTracker.Peek() : null;

            private string CurrentStartTagName => CurrentTracker?.TagName;

            public override SyntaxNode Visit(SyntaxNode node)
            {
                if (node != null)
                {
                    node = RewriteNode(node);
                }

                return base.Visit(node);
            }

            private SyntaxNode RewriteNode(SyntaxNode node)
            {
                if (node.IsToken ||
                    node is MarkupElementSyntax ||
                    node.Parent is MarkupElementSyntax)
                {
                    // This is either a token or we have already rewritten this node.
                    return node;
                }

                var currentIndex = 0;
                _startTagTracker.Clear();
                var children = node.ChildNodes();
                while (currentIndex < children.Count)
                {
                    var child = (RazorSyntaxNode)children[currentIndex];
                    if (!(child is MarkupTagBlockSyntax tagBlock) ||
                        tagBlock.Parent is MarkupElementSyntax)
                    {
                        // Not a node that we want to rewrite.
                        currentIndex++;
                        continue;
                    }

                    var tagName = tagBlock.GetTagName();
                    if (string.IsNullOrWhiteSpace(tagName) ||
                        IsVoidElement(tagBlock) ||
                        IsSelfClosing(tagBlock))
                    {
                        // Don't want to track incomplete, void or self-closing tags.
                        // Simply wrap it in a block with no body or end tag.
                        node = RewriteNodeCore(node, startTag: tagBlock, tagChildren: Enumerable.Empty<RazorSyntaxNode>(), endTag: null);

                        // Since we rewrote 'node', it's children are different. Update our collection.
                        children = node.ChildNodes();
                    }
                    else if (IsEndTag(tagBlock))
                    {
                        if (!string.IsNullOrEmpty(CurrentStartTagName) &&
                            string.Equals(CurrentStartTagName, tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            var startTagTracker = _startTagTracker.Pop();
                            var startTagIndex = startTagTracker.Index;
                            var startTag = (MarkupTagBlockSyntax)children[startTagIndex];

                            // Get the nodes between the start and the end tag.
                            var tagChildren = children.Skip(startTagIndex + 1).Take(currentIndex - startTagIndex - 1).Cast<RazorSyntaxNode>();

                            node = RewriteNodeCore(node, startTag, tagChildren, endTag: tagBlock);

                            // Set the index back to the start tag's position.
                            currentIndex = startTagTracker.Index;
                        }
                        else
                        {
                            // Current tag scope does not match the end tag. Attempt to recover the start tag
                            // by looking up the previous tag scopes for a matching start tag.
                            if (TryRecoverStartTag(node, tagName, tagBlock, currentIndex, out var rewritten, out var startTagIndex))
                            {
                                node = rewritten;

                                // Set the index back to the start tag's position.
                                currentIndex = startTagIndex;
                            }
                            else
                            {
                                // Could not recover. The end tag doesn't have a corresponding start tag. Wrap it in a block and move on.
                                node = RewriteNodeCore(node, startTag: null, tagChildren: Enumerable.Empty<RazorSyntaxNode>(), endTag: tagBlock);
                            }
                        }

                        // Since we rewrote 'node', it's children are different. Update our collection.
                        children = node.ChildNodes();
                    }
                    else
                    {
                        // This is a start tag. Keep track of it.
                        _startTagTracker.Push(new TagBlockTracker(tagBlock, node, currentIndex));
                    }

                    currentIndex++;
                }

                while (_startTagTracker.Count > 0)
                {
                    // We reached the end of the list and still have unmatched start tags
                    var startTagTracker = _startTagTracker.Pop();
                    var startTagIndex = startTagTracker.Index;
                    var startTag = (MarkupTagBlockSyntax)children[startTagIndex];
                    var tagChildren = children.Skip(startTagIndex + 1).Cast<RazorSyntaxNode>();

                    node = RewriteNodeCore(node, startTag, tagChildren, endTag: null);

                    // Since we rewrote 'node', it's children are different. Update our collection.
                    children = node.ChildNodes();
                }

                return node;
            }

            private SyntaxNode RewriteNodeCore(
                SyntaxNode parent,
                MarkupTagBlockSyntax startTag,
                IEnumerable<RazorSyntaxNode> tagChildren,
                MarkupTagBlockSyntax endTag)
            {
                var body = new SyntaxList<RazorSyntaxNode>(tagChildren);
                var markupElement = SyntaxFactory.MarkupElement((MarkupTagBlockSyntax)startTag, body, endTag);

                // Build a list of the original nodes that we want to replace with the rewritten element.
                var originalNodes = new List<SyntaxNode>();
                if (startTag != null)
                {
                    originalNodes.Add(startTag);
                }
                originalNodes.AddRange(tagChildren);
                if (endTag != null)
                {
                    originalNodes.Add(endTag);
                }

                if (originalNodes.Count == 0)
                {
                    // Nothing to replace
                    return parent;
                }

                // Replace nodes
                var rewrittenElement = parent.ReplaceNodes(originalNodes, (original, rewritten) => {
                    if (original == originalNodes[0] || rewritten == originalNodes[0])
                    {
                        // Put the new element in place of the first original node to be replaced.
                        return markupElement;
                    }

                    return null;
                });

                return rewrittenElement;
            }

            private bool TryRecoverStartTag(
                SyntaxNode parent,
                string tagName,
                MarkupTagBlockSyntax endTag,
                int endTagIndex,
                out SyntaxNode rewritten,
                out int startTagIndex)
            {
                rewritten = parent;
                startTagIndex = -1;
                var malformedTagCount = 0;

                foreach (var tracker in _startTagTracker)
                {
                    if (tracker.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    malformedTagCount++;
                }

                if (malformedTagCount != _startTagTracker.Count)
                {
                    parent = RewriteMalformedTags(parent, malformedTagCount);

                    // One final rewrite, this is the rewrite that completes our target tag which is not malformed.
                    var children = parent.ChildNodes();

                    var startTagTracker = _startTagTracker.Pop();
                    startTagIndex = startTagTracker.Index;
                    var startTag = startTagTracker.TagBlock;
                    
                    // Get the nodes between the start and the end tag.
                    var tagChildren = children.Skip(startTagIndex + 1).Take(endTagIndex - startTagIndex - 1).Cast<RazorSyntaxNode>();

                    rewritten = RewriteNodeCore(parent, startTag, tagChildren, endTag);

                    // We were able to recover
                    return true;
                }

                // Could not recover tag. Aka we found an end tag without a corresponding start tag.
                return false;
            }

            private SyntaxNode RewriteMalformedTags(SyntaxNode parent, int malformedTagCount)
            {
                for (var i = 0; i < malformedTagCount; i++)
                {
                    var children = parent.ChildNodes();
                    var startTagTracker = _startTagTracker.Pop();
                    var startTagIndex = startTagTracker.Index;
                    var startTag = (MarkupTagBlockSyntax)children[startTagIndex];
                    
                    parent = RewriteNodeCore(parent, startTag, Enumerable.Empty<RazorSyntaxNode>(), endTag: null);
                }

                return parent;
            }

            private bool IsEndTag(MarkupTagBlockSyntax tagBlock)
            {
                var childSpan = (MarkupTextLiteralSyntax)tagBlock.Children.First();

                // We grab the token that could be forward slash
                var relevantToken = childSpan.LiteralTokens[childSpan.LiteralTokens.Count == 1 ? 0 : 1];

                return relevantToken.Kind == SyntaxKind.ForwardSlash;
            }

            private bool IsVoidElement(MarkupTagBlockSyntax tagBlock)
            {
                return VoidElements.Contains(tagBlock.GetTagName(), StringComparer.OrdinalIgnoreCase);
            }

            private bool IsSelfClosing(MarkupTagBlockSyntax tagBlock)
            {
                var lastChild = tagBlock.ChildNodes().LastOrDefault();

                return lastChild?.GetContent().EndsWith("/>", StringComparison.Ordinal) ?? false;
            }

            private class TagBlockTracker
            {
                public TagBlockTracker(MarkupTagBlockSyntax tagBlock, SyntaxNode parent, int index)
                {
                    TagBlock = tagBlock;
                    Parent = parent;
                    Index = index;
                    TagName = tagBlock.GetTagName();
                }

                public MarkupTagBlockSyntax TagBlock { get; }

                public string TagName { get; }

                public SyntaxNode Parent { get; }

                public int Index { get; }
            }
        }

        private class Remover : SyntaxRewriter
        {
            public override SyntaxNode Visit(SyntaxNode node)
            {
                if (node != null)
                {
                    node = RewriteNode(node);
                }

                return base.Visit(node);
            }

            private SyntaxNode RewriteNode(SyntaxNode node)
            {
                if (node.IsToken)
                {
                    return node;
                }

                var currentIndex = 0;
                var children = node.ChildNodes();
                while (currentIndex < children.Count)
                {
                    var child = children[currentIndex];
                    if (!(child is MarkupElementSyntax tagElement))
                    {
                        currentIndex++;
                        continue;
                    }

                    node = node.ReplaceNode(tagElement, tagElement.ChildNodes());

                    // Since we rewrote 'node', it's children are different. Update our collection.
                    children = node.ChildNodes();

                    currentIndex++;
                }

                return node;
            }
        }
    }
}
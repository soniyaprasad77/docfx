// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using Markdig;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Microsoft.DocAsCode.MarkdigEngine.Extensions;

namespace Microsoft.Docs.Build
{
    internal static class MarkdownUtility
    {
        // This magic string identifies if an URL was a relative URL in source,
        // URLs starting with this magic string are transformed into relative URL after markup.
        private const string RelativeUrlMarker = "//////";

        private static readonly MarkdownPipeline[] s_markdownPipelines = new[]
        {
            CreateMarkdownPipeline(),
            CreateInlineMarkdownPipeline(),
            CreateTocMarkdownPipeline(),
        };

        private static readonly ThreadLocal<Stack<Status>> t_status = new ThreadLocal<Stack<Status>>(() => new Stack<Status>());

        public static (List<Error> errors, MarkdownDocument ast) Parse(string content, MarkdownPipelineType piplineType)
        {
            try
            {
                var status = new Status { Errors = new List<Error>() };

                t_status.Value.Push(status);

                var ast = Markdown.Parse(content, s_markdownPipelines[(int)piplineType]);

                return (status.Errors, ast);
            }
            finally
            {
                t_status.Value.Pop();
            }
        }

        public static (List<Error> errors, string html) ToHtml(
            Context context,
            string markdown,
            Document file,
            MarkdownPipelineType pipelineType)
        {
            using (InclusionContext.PushFile(file))
            {
                try
                {
                    var status = new Status
                    {
                        Errors = new List<Error>(),
                        Context = context,
                    };

                    t_status.Value.Push(status);

                    var html = Markdown.ToHtml(markdown, s_markdownPipelines[(int)pipelineType]);

                    var htmlWithRelativeLink = HtmlUtility.TransformLinks(html, (href, _) =>
                    {
                        if (href.StartsWith(RelativeUrlMarker))
                        {
                            return UrlUtility.GetRelativeUrl(file.SiteUrl, href.Substring(RelativeUrlMarker.Length));
                        }
                        return href;
                    });

                    return (status.Errors, htmlWithRelativeLink);
                }
                finally
                {
                    t_status.Value.Pop();
                }
            }
        }

        internal static void LogError(Error error)
        {
            t_status.Value.Peek().Errors.Add(error);
        }

        internal static string GetLink(string path, object relativeTo, MarkdownObject origin, int columnOffset = 0)
        {
            var status = t_status.Value.Peek();
            var (error, link, file) = status.Context.DependencyResolver.ResolveAbsoluteLink(new SourceInfo<string>(path, origin.ToSourceInfo(columnOffset: columnOffset)), (Document)relativeTo);
            status.Errors.AddIfNotNull(error);

            if (file != null)
            {
                link = RelativeUrlMarker + link;
            }

            return link;
        }

        internal static (Error error, string href, string display, Document file) ResolveXref(string href, MarkdownObject origin)
        {
            // TODO: now markdig engine combines all kinds of reference with inclusion, we need to split them out
            var (error, link, display, spec) = t_status.Value.Peek().Context.DependencyResolver.ResolveAbsoluteXref(new SourceInfo<string>(href, origin.ToSourceInfo()), (Document)InclusionContext.File);

            if (spec?.DeclairingFile != null)
            {
                link = RelativeUrlMarker + link;
            }

            return (error, link, display, spec?.DeclairingFile);
        }

        private static MarkdownPipeline CreateMarkdownPipeline()
        {
            var markdownContext = new MarkdownContext(GetToken, LogWarning, LogError, ReadFile);

            return new MarkdownPipelineBuilder()
                .UseYamlFrontMatter()
                .UseDocfxExtensions(markdownContext)
                .UseResolveLink()
                .UseResolveXref(ResolveXref)
                .UseMonikerZone(ParseMonikerRange)
                .Build();
        }

        private static MarkdownPipeline CreateInlineMarkdownPipeline()
        {
            var markdownContext = new MarkdownContext(GetToken, LogWarning, LogError, ReadFile);

            return new MarkdownPipelineBuilder()
                .UseYamlFrontMatter()
                .UseDocfxExtensions(markdownContext)
                .UseResolveLink()
                .UseResolveXref(ResolveXref)
                .UseMonikerZone(ParseMonikerRange)
                .UseInlineOnly()
                .Build();
        }

        private static MarkdownPipeline CreateTocMarkdownPipeline()
        {
            var builder = new MarkdownPipelineBuilder();

            // Only supports heading block and link inline
            builder.BlockParsers.RemoveAll(parser => !(
                parser is HeadingBlockParser || parser is ParagraphBlockParser ||
                parser is ThematicBreakParser || parser is HtmlBlockParser));

            builder.InlineParsers.RemoveAll(parser => !(parser is LinkInlineParser));

            builder.BlockParsers.Find<HeadingBlockParser>().MaxLeadingCount = int.MaxValue;

            builder.UseYamlFrontMatter()
                   .UseXref()
                   .UsePreciseSourceLocation();

            return builder.Build();
        }

        private static string GetToken(string key)
        {
            return t_status.Value.Peek().Context.TemplateEngine.GetToken(key);
        }

        private static void LogError(string code, string message, MarkdownObject origin, int? line)
        {
            t_status.Value.Peek().Errors.Add(new Error(ErrorLevel.Error, code, message, origin.ToSourceInfo(line)));
        }

        private static void LogWarning(string code, string message, MarkdownObject origin, int? line)
        {
            t_status.Value.Peek().Errors.Add(new Error(ErrorLevel.Warning, code, message, origin.ToSourceInfo(line)));
        }

        private static (string content, object file) ReadFile(string path, object relativeTo, MarkdownObject origin)
        {
            var status = t_status.Value.Peek();
            var (error, content, file) = status.Context.DependencyResolver.ResolveContent(new SourceInfo<string>(path, origin.ToSourceInfo()), (Document)relativeTo);
            status.Errors.AddIfNotNull(error);
            return (content, file);
        }

        private static List<string> ParseMonikerRange(SourceInfo<string> monikerRange)
        {
            var status = t_status.Value.Peek();
            var (error, monikers) = status.Context.MonikerProvider.GetZoneLevelMonikers((Document)InclusionContext.File, monikerRange);
            status.Errors.AddIfNotNull(error);
            return monikers;
        }

        private sealed class Status
        {
            public List<Error> Errors;

            public Context Context;
        }
    }
}
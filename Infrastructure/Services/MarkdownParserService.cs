#region Usings
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using TL;
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Service for parsing markdown text and converting it to Telegram message entities.
    /// Supports Markdown V1 syntax for Telegram compatibility.
    /// </summary>
    public class MarkdownParserService
    {
        private readonly ILogger<MarkdownParserService> _logger;
        private readonly MarkdownPipeline _pipeline;

        public MarkdownParserService(ILogger<MarkdownParserService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Configure Markdig pipeline for Telegram-compatible markdown parsing
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
        }

        /// <summary>
        /// Parses markdown text and converts it to plain text with corresponding Telegram message entities.
        /// </summary>
        /// <param name="markdownText">The markdown text to parse</param>
        /// <returns>A tuple containing the plain text and message entities</returns>
        public (string plainText, MessageEntity[] entities) ParseMarkdownToTelegramEntities(string markdownText)
        {
            if (string.IsNullOrEmpty(markdownText))
            {
                return (string.Empty, Array.Empty<MessageEntity>());
            }

            try
            {
                _logger.LogDebug("Parsing markdown text of length {Length} to Telegram entities", markdownText.Length);

                // Parse the markdown document - use fully qualified name to avoid ambiguity
                MarkdownDocument document = Markdig.Markdown.Parse(markdownText, _pipeline);

                StringBuilder plainText = new();
                List<MessageEntity> entities = [];
                int currentOffset = 0;

                // Process the document and extract entities
                ProcessMarkdownDocument(document, plainText, entities, ref currentOffset);

                string resultText = plainText.ToString();
                MessageEntity[] resultEntities = entities.ToArray();

                _logger.LogDebug("Successfully parsed markdown. Plain text length: {PlainTextLength}, Entities count: {EntitiesCount}",
                    resultText.Length, resultEntities.Length);

                return (resultText, resultEntities);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing markdown text to Telegram entities");
                // Return the original text without entities as fallback
                return (markdownText, Array.Empty<MessageEntity>());
            }
        }

        /// <summary>
        /// Simple console application entry point for testing markdown parsing functionality.
        /// This can be used when the file is compiled as a standalone program for testing.
        /// </summary>
        public static void Main(string[] args)
        {
            Console.WriteLine("Telegram Markdown V1 Parser Test");
            Console.WriteLine("================================\n");

            // Create a simple logger for demonstration
            using ILoggerFactory loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                builder.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
            ILogger<MarkdownParserService> logger = loggerFactory.CreateLogger<MarkdownParserService>();

            // Run the demonstration
            DemonstrateMarkdownParsing(logger);

            Console.WriteLine("\nPress any key to exit...");
            _ = Console.ReadKey();
        }

        /// <summary>
        /// Static method to demonstrate markdown parsing functionality.
        /// This can be called independently for testing and demonstration purposes.
        /// </summary>
        /// <param name="logger">Optional logger for output</param>
        public static void DemonstrateMarkdownParsing(ILogger<MarkdownParserService>? logger = null)
        {
            MarkdownParserService testService = new(logger ?? new Microsoft.Extensions.Logging.Abstractions.NullLogger<MarkdownParserService>());

            Console.WriteLine("=== Telegram Markdown V1 Parsing Demonstration ===\n");

            Dictionary<string, (string plainText, MessageEntity[] entities)> testCases = testService.TestMarkdownParsing();

            foreach (KeyValuePair<string, (string plainText, MessageEntity[] entities)> testCase in testCases)
            {
                Console.WriteLine($"Test Case: {testCase.Key}");
                Console.WriteLine($"Plain Text: {testCase.Value.plainText}");
                Console.WriteLine($"Entities Count: {testCase.Value.entities.Length}");

                if (testCase.Value.entities.Length > 0)
                {
                    Console.WriteLine("Entities:");
                    foreach (MessageEntity entity in testCase.Value.entities)
                    {
                        string entityType = entity.GetType().Name.Replace("MessageEntity", "");
                        Console.WriteLine($"  - {entityType}: Offset={entity.Offset}, Length={entity.Length}");

                        if (entity is MessageEntityTextUrl textUrl)
                        {
                            Console.WriteLine($"    URL: {textUrl.url}");
                        }
                        else if (entity is MessageEntityPre pre)
                        {
                            Console.WriteLine($"    Language: {pre.language}");
                        }
                    }
                }

                Console.WriteLine(new string('-', 50));
            }

            // Test specific markdown scenarios
            Console.WriteLine("\n=== Specific Markdown Scenarios ===\n");

            (string, string)[] scenarios = new[]
            {
                ("Bold Text", "**This is bold text**"),
                ("Italic Text", "*This is italic text*"),
                ("Bold and Italic", "**Bold** and *italic* text"),
                ("Code Inline", "Use `var message = \"Hello\";` in your code"),
                ("Link", "[Telegram](https://telegram.org) is awesome"),
                ("Header", "# Main Header\nThis is content"),
                ("Mixed", "# Welcome\nThis is **bold** and *italic* with `code` and [link](https://example.com)")
            };

            foreach ((string? name, string? markdown) in scenarios)
            {
                (string? plainText, MessageEntity[]? entities) = testService.ParseMarkdownToTelegramEntities(markdown);
                Console.WriteLine($"{name}:");
                Console.WriteLine($"  Input: {markdown}");
                Console.WriteLine($"  Output: {plainText}");
                Console.WriteLine($"  Entities: {entities.Length}");
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Test method to demonstrate and verify markdown parsing functionality.
        /// This method can be used to test various markdown scenarios and verify the output.
        /// </summary>
        /// <returns>A dictionary containing test cases and their results</returns>
        public Dictionary<string, (string plainText, MessageEntity[] entities)> TestMarkdownParsing()
        {
            Dictionary<string, (string plainText, MessageEntity[] entities)> testCases = new()
            {
                // Test case 1: Basic bold and italic
                ["Basic Formatting"] = ParseMarkdownToTelegramEntities("**Bold text** and *italic text*"),

                // Test case 2: Headers
                ["Headers"] = ParseMarkdownToTelegramEntities("# Main Header\n## Sub Header\n### Small Header"),

                // Test case 3: Code blocks
                ["Code Blocks"] = ParseMarkdownToTelegramEntities("```csharp\nvar message = \"Hello World\";\n```"),

                // Test case 4: Inline code
                ["Inline Code"] = ParseMarkdownToTelegramEntities("Use the `SendMessageAsync` method to send messages."),

                // Test case 5: Links
                ["Links"] = ParseMarkdownToTelegramEntities("[Visit our website](https://example.com)"),

                // Test case 6: Mixed content
                ["Mixed Content"] = ParseMarkdownToTelegramEntities("# Welcome\nThis is **bold** and *italic* text with `code` and [links](https://telegram.org)."),

                // Test case 7: Lists
                ["Lists"] = ParseMarkdownToTelegramEntities("- Item 1\n- **Bold Item 2**\n- *Italic Item 3*"),

                // Test case 8: No markdown
                ["Plain Text"] = ParseMarkdownToTelegramEntities("This is just plain text without any markdown formatting."),

                // Test case 9: Complex nested formatting
                ["Nested Formatting"] = ParseMarkdownToTelegramEntities("**Bold with *italic inside* and `code`**"),

                // Test case 10: Empty string
                ["Empty String"] = ParseMarkdownToTelegramEntities("")
            };

            _logger.LogInformation("Markdown parsing test completed. Processed {TestCount} test cases.", testCases.Count);

            return testCases;
        }

        /// <summary>
        /// Processes the markdown document and extracts entities recursively.
        /// </summary>
        private void ProcessMarkdownDocument(MarkdownDocument document, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            foreach (Block block in document)
            {
                ProcessBlock(block, plainText, entities, ref currentOffset);
            }
        }

        /// <summary>
        /// Processes a markdown block element.
        /// </summary>
        private void ProcessBlock(Block block, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    ProcessParagraph(paragraph, plainText, entities, ref currentOffset);
                    break;
                case HeadingBlock heading:
                    ProcessHeading(heading, plainText, entities, ref currentOffset);
                    break;
                case FencedCodeBlock codeBlock:
                    ProcessCodeBlock(codeBlock, plainText, entities, ref currentOffset);
                    break;
                case ListBlock listBlock:
                    ProcessListBlock(listBlock, plainText, entities, ref currentOffset);
                    break;
                case QuoteBlock quoteBlock:
                    ProcessQuoteBlock(quoteBlock, plainText, entities, ref currentOffset);
                    break;
                default:
                    // For other block types, do nothing (no Inline processing)
                    break;
            }
        }

        /// <summary>
        /// Processes a paragraph block.
        /// </summary>
        private void ProcessParagraph(ParagraphBlock paragraph, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            if (paragraph.Inline != null)
            {
                ProcessInline(paragraph.Inline, plainText, entities, ref currentOffset);
            }

            // Add newline after paragraph
            _ = plainText.AppendLine();
            currentOffset += Environment.NewLine.Length;
        }

        /// <summary>
        /// Processes a heading block.
        /// </summary>
        private void ProcessHeading(HeadingBlock heading, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            if (heading.Inline != null)
            {
                // Add bold formatting for headings
                int startOffset = currentOffset;
                ProcessInline(heading.Inline, plainText, entities, ref currentOffset);
                int endOffset = currentOffset;

                if (endOffset > startOffset)
                {
                    entities.Add(new MessageEntityBold
                    {
                        Offset = startOffset,
                        Length = endOffset - startOffset
                    });
                }
            }

            // Add newline after heading
            _ = plainText.AppendLine();
            currentOffset += Environment.NewLine.Length;
        }

        /// <summary>
        /// Processes a code block.
        /// </summary>
        private void ProcessCodeBlock(FencedCodeBlock codeBlock, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            int startOffset = currentOffset;
            string codeText = codeBlock.Lines.ToString();

            // Add language identifier if present
            if (!string.IsNullOrEmpty(codeBlock.Info))
            {
                _ = plainText.AppendLine(codeBlock.Info);
                currentOffset += codeBlock.Info.Length + Environment.NewLine.Length;
            }

            _ = plainText.Append(codeText);
            currentOffset += codeText.Length;

            int endOffset = currentOffset;

            if (endOffset > startOffset)
            {
                entities.Add(new MessageEntityPre
                {
                    Offset = startOffset,
                    Length = endOffset - startOffset,
                    language = codeBlock.Info ?? string.Empty
                });
            }

            _ = plainText.AppendLine();
            currentOffset += Environment.NewLine.Length;
        }

        /// <summary>
        /// Processes a list block.
        /// </summary>
        private void ProcessListBlock(ListBlock listBlock, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            foreach (Block item in listBlock)
            {
                if (item is ListItemBlock listItem)
                {
                    // Add bullet point
                    _ = plainText.Append("• ");
                    currentOffset += 2;

                    // Process all children of the list item
                    foreach (Block child in listItem)
                    {
                        if (child is ParagraphBlock paragraph)
                        {
                            if (paragraph.Inline != null)
                            {
                                ProcessInline(paragraph.Inline, plainText, entities, ref currentOffset);
                            }
                        }
                        else if (child is Block block)
                        {
                            ProcessBlock(block, plainText, entities, ref currentOffset);
                        }
                    }

                    _ = plainText.AppendLine();
                    currentOffset += Environment.NewLine.Length;
                }
            }
        }

        /// <summary>
        /// Processes a quote block.
        /// </summary>
        private void ProcessQuoteBlock(QuoteBlock quoteBlock, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            foreach (Block item in quoteBlock)
            {
                if (item is ParagraphBlock paragraph)
                {
                    // Add quote prefix
                    _ = plainText.Append("> ");
                    currentOffset += 2;

                    ProcessParagraph(paragraph, plainText, entities, ref currentOffset);
                }
            }
        }

        /// <summary>
        /// Processes inline markdown elements.
        /// </summary>
        private void ProcessInline(ContainerInline container, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            foreach (Inline inline in container)
            {
                ProcessInlineElement(inline, plainText, entities, ref currentOffset);
            }
        }

        /// <summary>
        /// Processes individual inline markdown elements.
        /// </summary>
        private void ProcessInlineElement(Inline inline, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    string text = literal.Content.ToString();
                    _ = plainText.Append(text);
                    currentOffset += text.Length;
                    break;

                case EmphasisInline emphasis:
                    ProcessEmphasis(emphasis, plainText, entities, ref currentOffset);
                    break;

                case CodeInline code:
                    ProcessInlineCode(code, plainText, entities, ref currentOffset);
                    break;

                case LinkInline link:
                    ProcessLink(link, plainText, entities, ref currentOffset);
                    break;

                case LineBreakInline:
                    _ = plainText.AppendLine();
                    currentOffset += Environment.NewLine.Length;
                    break;

                case ContainerInline container:
                    ProcessInline(container, plainText, entities, ref currentOffset);
                    break;

                default:
                    // For other inline types, try to extract text content
                    if (inline is LiteralInline literalInline)
                    {
                        string content = literalInline.Content.ToString();
                        _ = plainText.Append(content);
                        currentOffset += content.Length;
                    }
                    break;
            }
        }

        /// <summary>
        /// Processes emphasis (bold/italic) elements.
        /// </summary>
        private void ProcessEmphasis(EmphasisInline emphasis, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            int startOffset = currentOffset;

            // Process the content inside emphasis
            foreach (Inline child in emphasis)
            {
                ProcessInlineElement(child, plainText, entities, ref currentOffset);
            }

            int endOffset = currentOffset;

            if (endOffset > startOffset)
            {
                // Determine if it's bold or italic based on delimiter count
                int delimiterCount = emphasis.DelimiterCount;

                if (delimiterCount >= 2)
                {
                    // Bold
                    entities.Add(new MessageEntityBold
                    {
                        Offset = startOffset,
                        Length = endOffset - startOffset
                    });
                }
                else
                {
                    // Italic
                    entities.Add(new MessageEntityItalic
                    {
                        Offset = startOffset,
                        Length = endOffset - startOffset
                    });
                }
            }
        }

        /// <summary>
        /// Processes inline code elements.
        /// </summary>
        private void ProcessInlineCode(CodeInline code, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            int startOffset = currentOffset;
            string codeText = code.Content.ToString();

            _ = plainText.Append(codeText);
            currentOffset += codeText.Length;

            int endOffset = currentOffset;

            if (endOffset > startOffset)
            {
                entities.Add(new MessageEntityCode
                {
                    Offset = startOffset,
                    Length = endOffset - startOffset
                });
            }
        }

        /// <summary>
        /// Processes link elements.
        /// </summary>
        private void ProcessLink(LinkInline link, StringBuilder plainText, List<MessageEntity> entities, ref int currentOffset)
        {
            int startOffset = currentOffset;

            // Process the content inside the link
            foreach (Inline child in link)
            {
                ProcessInlineElement(child, plainText, entities, ref currentOffset);
            }

            int endOffset = currentOffset;

            if (endOffset > startOffset && !string.IsNullOrEmpty(link.Url))
            {
                entities.Add(new MessageEntityTextUrl
                {
                    Offset = startOffset,
                    Length = endOffset - startOffset,
                    url = link.Url
                });
            }
        }

        /// <summary>
        /// Escapes special characters for Telegram markdown compatibility.
        /// </summary>
        /// <param name="text">Text to escape</param>
        /// <returns>Escaped text</returns>
        public string EscapeMarkdownV1(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Characters that need to be escaped in Telegram Markdown V1
            char[] specialChars = new[] { '_', '*', '`', '[', ']', '(', ')', '~', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

            string result = text;
            foreach (char ch in specialChars)
            {
                result = result.Replace(ch.ToString(), "\\" + ch);
            }

            return result;
        }

        /// <summary>
        /// Converts plain text with markdown-style formatting to Telegram entities.
        /// This is a simpler alternative to full markdown parsing.
        /// </summary>
        /// <param name="text">Text with markdown-style formatting</param>
        /// <returns>Tuple of plain text and entities</returns>
        public (string plainText, MessageEntity[] entities) ParseSimpleMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return (string.Empty, Array.Empty<MessageEntity>());
            }

            List<MessageEntity> entities = [];
            _ = new StringBuilder();

            // Regex patterns for different markdown elements
            var patterns = new[]
            {
                // Bold: **text** or __text__
                new { Pattern = @"\*\*(.*?)\*\*|__(.*?)__", EntityType = "bold" },
                // Italic: *text* or _text_
                new { Pattern = @"\*(.*?)\*|_(.*?)_", EntityType = "italic" },
                // Code: `text`
                new { Pattern = @"`(.*?)`", EntityType = "code" },
                // Links: [text](url)
                new { Pattern = @"\[(.*?)\]\((.*?)\)", EntityType = "link" }
            };

            string processedText = text;
            int offsetAdjustment = 0;

            foreach (var pattern in patterns)
            {
                MatchCollection matches = Regex.Matches(processedText, pattern.Pattern, RegexOptions.Singleline);

                foreach (Match match in matches.Reverse()) // Process in reverse to maintain offsets
                {
                    int startOffset = match.Index + offsetAdjustment;
                    string content = match.Groups[1].Value;
                    int length = content.Length;

                    // Replace the markdown with plain text
                    processedText = processedText.Remove(match.Index, match.Length).Insert(match.Index, content);
                    offsetAdjustment -= match.Length - content.Length;

                    // Create appropriate entity
                    MessageEntity? entity = pattern.EntityType switch
                    {
                        "bold" => new MessageEntityBold { Offset = startOffset, Length = length },
                        "italic" => new MessageEntityItalic { Offset = startOffset, Length = length },
                        "code" => new MessageEntityCode { Offset = startOffset, Length = length },
                        "link" => new MessageEntityTextUrl
                        {
                            Offset = startOffset,
                            Length = length,
                            url = match.Groups[2].Value
                        },
                        _ => null
                    };

                    if (entity != null)
                    {
                        entities.Add(entity);
                    }
                }
            }

            return (processedText, entities.ToArray());
        }
    }
}
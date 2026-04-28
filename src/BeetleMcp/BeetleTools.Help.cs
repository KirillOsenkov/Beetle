using System;
using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;

namespace BeetleMcp;

[McpServerToolType]
public static class BeetleHelp
{
    private const string LlmGuideResourceName = "LlmGuide.md";

    private static string? llmGuideText;

    private static string LlmGuideText
    {
        get
        {
            if (llmGuideText != null)
            {
                return llmGuideText;
            }

            var assembly = typeof(BeetleHelp).Assembly;
            using var stream = assembly.GetManifestResourceStream(LlmGuideResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{LlmGuideResourceName}' not found in {assembly.FullName}.");
            using var reader = new StreamReader(stream);
            llmGuideText = reader.ReadToEnd();
            return llmGuideText;
        }
    }

    [McpServerTool(Name = "get_llm_guide", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns the BeetleMcp field manual for LLMs: workflow, identity rules, filter reference, recipes (cold-start triage, good-vs-bad diff, correlate-with-test-log-timestamp, root-cause walk-back), and pitfalls.

Call this once at the start of a session — or whenever you're unsure how to approach a .beetle file — before issuing other tool calls.")]
    public static string GetLlmGuide() => LlmGuideText;
}

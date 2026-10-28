// <copyright file="CopilotJsonEventExtractor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>Copilot の JSON イベントから assistant の文章だけを取り出す.</summary>
internal sealed class CopilotJsonEventExtractor
{
    private readonly HashSet<string> _streamedMessages = [];

    public string? Extract(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? type = typeElement.GetString();
            string? id = ReadString(data, "messageId");
            if (type == "assistant.message_delta")
            {
                string? delta = ReadString(data, "deltaContent");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(delta))
                {
                    _streamedMessages.Add(id);
                    return delta;
                }
            }
            else if (type == "assistant.message")
            {
                if (!string.IsNullOrEmpty(id) && _streamedMessages.Remove(id))
                {
                    return null;
                }

                return ReadString(data, "content");
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

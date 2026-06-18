// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core.KnowledgePacks;

public enum KnowledgePackVersionOrder
{
    Older = -1,
    Same = 0,
    Newer = 1
}

public static class KnowledgePackVersion
{
    public static KnowledgePackVersionOrder Compare(string currentVersion, string candidateVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateVersion);

        var comparison = CompareCore(currentVersion, candidateVersion);
        return comparison < 0
            ? KnowledgePackVersionOrder.Newer
            : comparison > 0
                ? KnowledgePackVersionOrder.Older
                : KnowledgePackVersionOrder.Same;
    }

    private static int CompareCore(string currentVersion, string candidateVersion)
    {
        var current = Split(currentVersion);
        var candidate = Split(candidateVersion);
        var length = Math.Max(current.Length, candidate.Length);

        for (var i = 0; i < length; i++)
        {
            var left = i < current.Length ? current[i] : "0";
            var right = i < candidate.Length ? candidate[i] : "0";
            var result = CompareSegment(left, right);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static string[] Split(string version)
        => version.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int CompareSegment(string current, string candidate)
    {
        var currentIsNumber = long.TryParse(current, out var currentNumber);
        var candidateIsNumber = long.TryParse(candidate, out var candidateNumber);

        if (currentIsNumber && candidateIsNumber)
        {
            return currentNumber.CompareTo(candidateNumber);
        }

        return string.Compare(current, candidate, StringComparison.OrdinalIgnoreCase);
    }
}

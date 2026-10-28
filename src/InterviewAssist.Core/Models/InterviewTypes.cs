namespace InterviewAssist.Core.Models;

/// <summary>
/// One kind of interview round. <see cref="Guidance"/> goes into the system prompt and tunes who is listening, the tone
/// (how casual, how much humor) and how the answer reasons for that round. <see cref="Name"/> is the stored setting
/// value, so keep names stable.
/// </summary>
public sealed record InterviewTypeOption(string Name, string ShortName, string Description, string Guidance)
{
    public override string ToString() => Name;
}

public static class InterviewTypes
{
    public const string DefaultName = "General";

    public static readonly IReadOnlyList<InterviewTypeOption> All =
    [
        new(DefaultName, "General",
            "Mixed or unknown round. Default style, no special focus.",
            "Mixed or unknown round, so keep the default style. First work out what the question is really testing: fit, "
            + "skill, or a past story. Hit that head-on with one clear point and one concrete example from the resume or "
            + "senior experience. Story questions get a quick story; technical ones stay plain and simple."),

        new("Recruiter screen", "Recruiter",
            "First call with HR or a talent partner: fit, motivation, salary, logistics.",
            "A recruiter, usually non-technical, is checking basic fit, motivation, clear communication, salary and "
            + "logistics. Tone: the warmest, most upbeat round; slang and light humor welcome. Skip jargon and deep detail: "
            + "headline how your background matches the job's top needs, a sentence or two each, and show real interest in "
            + "this role. If pushed on pay, anchor to any posted range. Keep start date, notice and location answers short, "
            + "positive and general."),

        new("Hiring manager", "Manager",
            "Your future boss digs into your experience and how you'd solve the team's problems.",
            "Your future boss wants to know two things: can you fix my team's real problems, and would I like working with "
            + "you every day? Tone: confident, practical peer, moderately casual, light humor fine. Lead with your most "
            + "relevant win and its result, then say how you'd apply it to their situation and the job's main duties. For "
            + "first-90-days questions, sketch a simple arc: listen and learn, land a quick win, then scale."),

        new("Behavioral", "Behavioral",
            "\"Tell me about a time…\" questions on ownership, conflict, failure and teamwork.",
            "The interviewer reads your past behavior as a preview of future behavior: ownership, conflict, feedback, "
            + "failure, teamwork. Spot the trait the question targets, pick one story that proves it, and tell it short: "
            + "setup, stakes, what you did and the result, lesson. Say 'I', not 'we', so your own choices are clear. Tone: "
            + "honest and reflective; light self-deprecating humor suits failure stories. Own mistakes, never blame others."),

        new("Technical", "Technical",
            "Concepts, past project depth and troubleshooting, explained in plain words.",
            "An engineer or tech lead is probing real depth, hands-on experience and how you debug. Tone: calm and sure, "
            + "still casual but lighter on slang and jokes. Explain it like you're talking to a smart coworker, as flowing "
            + "talk, not a list: what it is, why it matters, a time you used it, and the main trade-off. For troubleshooting, "
            + "narrate your steps: rule out the obvious, narrow it down, fix it, stop it recurring."),

        new("System design", "Sys design",
            "Design a system out loud: requirements, main parts, scaling and trade-offs.",
            "A senior engineer is judging how you shape a vague problem and weigh trade-offs, not whether you know every "
            + "tool. Tone: relaxed whiteboard chat with a colleague, light slang. Open with one or two quick assumptions about "
            + "users, load and must-have features. Then talk through the big picture in plain words, no headings: how data "
            + "moves, where it lives, what happens under heavy traffic or failure. Name the key trade-off and why, then offer "
            + "to go deeper."),

        new("Live coding", "Coding",
            "Solve a coding problem live. The only round where answers include code.",
            "An engineer is watching how you solve, code and communicate under time pressure. Code is allowed and expected "
            + "here, overriding the no-code rule. Tone: focused, casual think-aloud. Restate the problem and key edge cases "
            + "in a sentence, then explain the approach in plain words. Give clean, readable code in one fenced block, in the "
            + "language asked or the job's main language. Finish with time and space complexity and one quick test case. "
            + "The code doesn't count toward the length limit."),

        new("Cultural fit", "Culture fit",
            "Values, teamwork and how you work with people. Would you fit the team?",
            "A future peer or HR partner is checking whether you'd be good to work with daily and share the team's values: "
            + "collaboration, feedback, disagreement, ownership. Tone: the most relaxed and personal round; humor welcome. "
            + "Prove values with quick real moments, not claims or full story arcs. Link them to the mission or values in the "
            + "job description. Admit something you're still working on to show humility. Never knock past teams."),

        new("Panel", "Panel",
            "Several interviewers at once, often mixing technical and business roles.",
            "Several people with different lenses, like engineering, management and cross-functional partners, are scoring "
            + "you at once. Tone: confident, clear, a notch more polished; humor only if it comes naturally. Answer whoever "
            + "asked, but make it land for everyone: plain-English bottom line first, then one concrete technical detail for "
            + "the engineers. Build on points made earlier to show you're listening. Keep answers tight so everyone gets a turn."),

        new("Final / executive", "Final",
            "Last round with a senior leader or exec: judgment, leadership, long-term fit.",
            "A senior leader is weighing your judgment, strategic thinking, leadership, and whether you'd raise the bar long "
            + "term. Tone: polished and confident but still conversational; little slang or humor, few laugh sounds. Lead "
            + "with the business outcome, then how you got there. Talk priorities, trade-offs, influencing people, and the "
            + "impact you'd make in year one. Tie it to where the company is heading, per the job description. If offer talk "
            + "comes up, stay enthusiastic and collaborative."),
    ];

    /// <summary>The option with this name (case-insensitive), or General when unknown/empty.</summary>
    public static InterviewTypeOption Find(string? name)
    {
        var key = name?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var option in All)
            {
                if (string.Equals(option.Name, key, StringComparison.OrdinalIgnoreCase))
                    return option;
            }
        }
        return All[0];
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// Hides /say messages from players who are NOT in the current HMS session, at the DISPLAY layer.
//
// PRIVACY: this does NOT collect, store, or transmit any chat. It subscribes to Dalamud's IChatGui.ChatMessage
// (messages the game is ALREADY about to display), and for /say from a non-session-member it calls PreventOriginal()
// to stop the game rendering that one line. Nothing is read into HMS state, nothing crosses the relay. It is a pure
// display filter - "don't show me strangers' /say while I'm in an RP session."
//
// Scope note (the architectural reality): your REAL character receives /say from players physically near your REAL
// body on the live server - NOT from your room members (their real characters are elsewhere; their session presence is
// a puppet at a synthetic position). So this filter suppresses real-proximity strangers' /say. It cannot MAKE room
// members' /say appear (that would require relaying chat content, which HMS deliberately does NOT do). It only hides.
public sealed class SayFilterService : IDisposable
{
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    // Supplies the set of session-member character names (lower-cased) whose /say should be ALLOWED. When the session
    // ends this returns empty, so the filter has nothing to allow - but it also only ACTS when Enabled, and Enabled is
    // cleared on session end, so an empty set never causes over-suppression outside a session.
    private readonly Func<HashSet<string>> sessionMemberNames;

    // The game's spatial-chat ranges (yalms), applied to synthetic distances so proximity chat feels real on synthetic
    // maps. Fixed game behavior, not preferences: /say close, /yell ~2.5-3x, /shout whole-area (never culled).
    // Calibrated from an in-game minimap capture (grid square = 50y): /say just under half a square, /yell ~2.75x say.
    private const float SayRangeYalms = 35f;
    private const float YellRangeYalms = 50f;

    // Active only while a session is running (set at session start, cleared on teardown). When active, the filter both
    // hides non-session /say (you're isolated in a session) AND range-culls members' /say/yell by synthetic distance.
    // One flag - these aren't independent options, they're the two halves of "spatial chat behaves correctly in-session."
    public bool Active;

    // Diagnostic (/hms saydiag): when true, log every chat message's kind/sender/text (observe-only, independent of
    // session). Also logs the range-cull decision (sender, member?, resolved distance, verdict) to debug culling.
    public bool Diag;

    private bool subscribed;

    // Returns the synthetic distance (yalms) from the local player to the named sender's PUPPET, or -1 if the sender
    // isn't a resolvable session peer. Local player's own /say returns 0 (always in range). Used for the range cull.
    private readonly Func<string, float> senderDistance;

    public SayFilterService(IChatGui chat, IPluginLog log, Func<HashSet<string>> sessionMemberNames,
        Func<string, float> senderDistance)
    {
        this.chat = chat;
        this.log = log;
        this.sessionMemberNames = sessionMemberNames;
        this.senderDistance = senderDistance;
    }

    public void Initialize()
    {
        if (subscribed) return;
        chat.ChatMessage += OnChatMessage;
        subscribed = true;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            // Diagnostic (/hms saydiag): log EVERY chat message's kind + sender + text, so we can see whether /say,
            // /yell, /shout reach the chat display at all - and whether HMS's firewall drops them (nothing logged in
            // session for a remote sender ⇒ dropped upstream). Logs regardless of Enabled/session; observe-only.
            if (Diag)
            {
                var s = ExtractName(message.Sender.TextValue);
                var m = message.Message.TextValue;
                if (m.Length > 40) m = m[..40] + "…";
                log.Information("[HMSync] [SAY-DIAG] kind=" + message.LogKind + " (" + (int)message.LogKind + ") sender='" + s + "' text='" + m + "'");
            }

            // Active only while a session runs. When active: hide non-session /say AND range-cull members' /say/yell.
            if (!Active) return;

            // Spatial chat only. Say/Yell/Shout are the proximity modes; everything else (tells, party, FC, LS,
            // system, etc.) is never filtered. Shout is whole-area - never range-culled, but still member-culled.
            bool isSay = message.LogKind == XivChatType.Say;
            bool isYell = message.LogKind == XivChatType.Yell;
            bool isShout = message.LogKind == XivChatType.Shout;
            if (!isSay && !isYell && !isShout) return;

            var senderName = ExtractName(message.Sender.TextValue);
            if (string.IsNullOrEmpty(senderName)) return;

            var isMember = sessionMemberNames().Contains(senderName.ToLowerInvariant());

            // Non-session-member spatial chat → always hidden (you're isolated in a session).
            if (!isMember)
            {
                if (Diag) log.Information($"[HMSync] [SAYCULL] '{senderName}' NOT a member → HIDE");
                message.PreventOriginal();
                return;
            }

            // Session member: range-cull their /say and /yell by synthetic distance (/shout is whole-area).
            if (!isShout)
            {
                float dist = senderDistance(senderName);   // -1 = unresolved (don't cull); 0 = local (in range)
                float range = isYell ? YellRangeYalms : SayRangeYalms;
                if (Diag) log.Information($"[HMSync] [SAYCULL] '{senderName}' member, kind={message.LogKind}, dist={dist:F1}, range={range} → {(dist >= 0 && dist > range ? "HIDE" : "SHOW")}");
                if (dist >= 0 && dist > range)
                {
                    message.PreventOriginal();
                    return;
                }
            }
            // Otherwise → allow.
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SayFilter error: " + ex.Message);
        }
    }

    // The Sender SeString often includes server/world glyphs and formatting; reduce to the bare character name for
    // matching. Names may carry a leading world icon or a party-number prefix - strip non-letter leading noise.
    private static string ExtractName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        // Drop any leading non-letter characters (icons, numbers) that can prefix a sender name.
        int start = 0;
        while (start < s.Length && !char.IsLetter(s[start])) start++;
        return start > 0 ? s[start..].Trim() : s;
    }

    public void Dispose()
    {
        if (subscribed)
        {
            chat.ChatMessage -= OnChatMessage;
            subscribed = false;
        }
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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

    // Chat-name replacement ("moniker in chat"). HMS syncs each session member's chosen Moniker name onto their
    // NAMEPLATE, but chat lines carry the real character's name, so the DISPLAYED chat name stays the real name. These
    // delegates close the gap at the display layer without HMS learning anything new. `monikerForRealName` maps ANY
    // real name — a session member's OR the local player's own — to the Moniker name to show, or null when there's no
    // override / the feature is off. `localPlayerName` supplies the host's own real character name, needed to locate
    // and swap the name in our OWN flat-printed lines (which carry no PlayerPayload to read it from). The plugin
    // supplies both (closing over the peer registry + the local Moniker name + the config toggle), so this service
    // keeps no Moniker/config dependency of its own. Null-safe: unwired = feature off.
    private readonly Func<string, string?>? monikerForRealName;
    private readonly Func<string?>? localPlayerName;

    public SayFilterService(IChatGui chat, IPluginLog log, Func<HashSet<string>> sessionMemberNames,
        Func<string, float> senderDistance, Func<string, string?>? monikerForRealName = null,
        Func<string?>? localPlayerName = null)
    {
        this.chat = chat;
        this.log = log;
        this.sessionMemberNames = sessionMemberNames;
        this.senderDistance = senderDistance;
        this.monikerForRealName = monikerForRealName;
        this.localPlayerName = localPlayerName;
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
                var rendered = ExtractName(message.Sender.TextValue);
                var pn = RealNameFromPayload(message.Sender);
                var m = message.Message.TextValue;
                if (m.Length > 40) m = m[..40] + "…";
                log.Information("[HMSync] [SAY-DIAG] kind=" + message.LogKind + " (" + (int)message.LogKind + ") rendered='" + rendered + "' payload='" + (pn ?? "<none>") + "' text='" + m + "'");
            }

            // Active only while a session runs. When active: hide non-session /say AND range-cull members' /say/yell,
            // and restamp session members' Moniker names across EVERY chat channel they can speak in.
            if (!Active) return;

            // Spatial chat = the proximity modes (say/yell/shout). These are the only channels we ever HIDE or
            // range-cull; everything else is moniker-restamped but never suppressed.
            bool isSay = message.LogKind == XivChatType.Say;
            bool isYell = message.LogKind == XivChatType.Yell;
            bool isShout = message.LogKind == XivChatType.Shout;
            bool isSpatial = isSay || isYell || isShout;
            // Emotes (/em custom + the standard emotes) carry the actor's name in the SENDER field and/or prepended
            // to the message BODY (it varies by emote type / game version). RewriteEmoteName restamps wherever it is.
            // Never culled (a member's emote always shows), so restamp and return.
            if (message.LogKind == XivChatType.CustomEmote || message.LogKind == XivChatType.StandardEmote)
            {
                RewriteEmoteName(message);
                return;
            }
            // Group channels (party, alliance, FC, linkshells, cross-world LS, novice network, PvP team) carry the
            // same clickable PlayerPayload sender as say/yell, so the SAME sender rewrite applies — but they are NOT
            // proximity chat, so they are NEVER hidden or range-culled, only moniker-restamped. monikerForRealName
            // returns null for anyone without a synced Moniker, so this only ever touches session members; strangers'
            // FC/party lines pass through untouched.
            bool isGroup = IsGroupChannel(message.LogKind);
            if (!isSpatial && !isGroup) return;

            // NB-17: COSMETIC-PROOF sender match. Match on the player's REAL identity from the SeString's PlayerPayload,
            // not the rendered TextValue. Nameplate/chat mods (Moniker, class-abbrev prefixers like "WAR Name",
            // Honorific-in-chat) rewrite the DISPLAYED sender, which broke the old TextValue match - a member whose chat
            // name showed a prefix never matched the real-name set, so their /say was wrongly HIDDEN. The PlayerPayload
            // carries the true name under any cosmetic. Everything else in HMS binds by ContentId; this is the one
            // name-keyed seam, and the payload is the closest stable identity available at the chat-display layer.
            //
            // Own-message handling: the local player's own flat-printed chat carries NO PlayerPayload (only remote
            // players' names are wrapped in a clickable PlayerPayload). So "spatial/group line with no payload" = your
            // own message → never cull it, just restamp it with our OWN Moniker so our chat matches our plate.
            var senderName = RealNameFromPayload(message.Sender);
            if (senderName == null)
            {
                if (Diag) log.Information("[HMSync] [SAYCULL] no PlayerPayload (own message) → SHOW");
                RewriteSender(message, monikerForRealName?.Invoke(localPlayerName?.Invoke() ?? ""));
                return;
            }
            if (string.IsNullOrEmpty(senderName)) return;

            var isMember = sessionMemberNames().Contains(senderName.ToLowerInvariant());

            // Hiding/culling is SPATIAL-ONLY. Group channels (party/FC/alliance/...) are never suppressed — a member
            // may be far away or off-map and their FC/party chat should still arrive.
            if (isSpatial)
            {
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
            }
            // Shown line (in-range say/yell, any shout, or any group-channel message) → restamp the displayed sender
            // with the speaker's synced Moniker name so chat matches the nameplate. No-op when they have no moniker or
            // the feature is off (so non-members in group channels are left exactly as the game rendered them).
            RewriteSender(message, monikerForRealName?.Invoke(senderName));
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SayFilter error: " + ex.Message);
        }
    }

    // Replace the displayed chat sender with the given Moniker name, IN PLACE (Dalamud SDK 15's IHandleableChatMessage
    // exposes a settable Sender + SenderModified flag). No-op when there is no moniker to apply. We deliberately emit a
    // PLAIN TextPayload rather than a PlayerPayload: the moniker is meant to MASK identity, and a clickable player link
    // would leak the real name via its tooltip/target. The cull above already resolved the REAL name from the original
    // payload, so replacing the sender here cannot affect member matching (it runs only on the show path, after).
    private void RewriteSender(IHandleableChatMessage message, string? moniker)
    {
        if (string.IsNullOrEmpty(moniker)) return;
        // Assigning Sender is what marks the message dirty (SenderModified is a read-only flag Dalamud sets from this
        // assignment); the game then re-encodes the sender from our replacement on display.
        try { message.Sender = new SeString(new TextPayload(moniker)); }
        catch (Exception ex) { log.Debug("[HMSync] chat-name rewrite failed: " + ex.Message); }
    }

    // Group chat channels that carry a session member's clickable PlayerPayload sender the same way say/yell do:
    // party (incl. cross-world), alliance, free company, the eight linkshells, the eight cross-world linkshells, the
    // novice network, and the PvP team channel. We moniker-restamp the sender on all of these but NEVER hide or
    // range-cull them (they aren't proximity chat). Tells are deliberately excluded: an outgoing tell's sender is a
    // ">> Target" glyph string rather than a plain player name, so a blind sender swap would mangle it. Anything not
    // listed here (system, echo, error, gil-recv, etc.) is left completely alone.
    private static bool IsGroupChannel(XivChatType kind)
    {
        switch (kind)
        {
            case XivChatType.Party:
            case XivChatType.CrossParty:
            case XivChatType.Alliance:
            case XivChatType.FreeCompany:
            case XivChatType.NoviceNetwork:
            case XivChatType.PvPTeam:
            case XivChatType.Ls1:
            case XivChatType.Ls2:
            case XivChatType.Ls3:
            case XivChatType.Ls4:
            case XivChatType.Ls5:
            case XivChatType.Ls6:
            case XivChatType.Ls7:
            case XivChatType.Ls8:
            case XivChatType.CrossLinkShell1:
            case XivChatType.CrossLinkShell2:
            case XivChatType.CrossLinkShell3:
            case XivChatType.CrossLinkShell4:
            case XivChatType.CrossLinkShell5:
            case XivChatType.CrossLinkShell6:
            case XivChatType.CrossLinkShell7:
            case XivChatType.CrossLinkShell8:
                return true;
            default:
                return false;
        }
    }

    // Restamp the actor's Moniker name on an EMOTE line (custom /em + standard emotes). The actor's real name can
    // appear in TWO places depending on emote type / game version: (a) the SENDER field (a clickable PlayerPayload for
    // a remote actor; a flat name string for your own line), and/or (b) prepended to the MESSAGE body ("<Name> does
    // something"). We restamp BOTH, since either may be the one the client renders — each branch is a no-op when the
    // name isn't there, so covering both cannot double-fabricate a name.
    //
    // Safety on the sender branch: we only stamp an empty/flat sender with our OWN moniker when the local player's real
    // name is actually PRESENT in that sender text. That prevents fabricating a bogus sender on emotes whose name lives
    // in the body instead (which would otherwise brand every emote — including strangers' — with our own moniker).
    // Rebuilt as plain text (drops the clickable link) — same privacy stance as the sender rewrite.
    private void RewriteEmoteName(IHandleableChatMessage message)
    {
        // (a) Sender field.
        var senderReal = RealNameFromPayload(message.Sender);
        if (senderReal != null)
        {
            // Remote actor — PlayerPayload carries their true name; stamp their synced Moniker.
            RewriteSender(message, monikerForRealName?.Invoke(senderReal));
        }
        else
        {
            // Flat/own line: stamp our own Moniker ONLY when our real name is genuinely in the sender text, so we
            // never brand a body-name emote (empty sender) with a spurious sender.
            var ownReal = localPlayerName?.Invoke();
            if (!string.IsNullOrEmpty(ownReal) &&
                message.Sender.TextValue.IndexOf(ownReal, StringComparison.Ordinal) >= 0)
                RewriteSender(message, monikerForRealName?.Invoke(ownReal));
        }

        // (b) Message body: name from a body PlayerPayload (remote actor), else our own (own lines are flat). Swap the
        // FIRST occurrence (the leading actor name); a later mention (e.g. an emote targeting the actor) is left alone.
        string? bodyReal = RealNameFromPayload(message.Message) ?? localPlayerName?.Invoke();
        if (string.IsNullOrEmpty(bodyReal)) return;
        var bodyMoniker = monikerForRealName?.Invoke(bodyReal);
        if (string.IsNullOrEmpty(bodyMoniker)) return;
        var text = message.Message.TextValue;
        int i = text.IndexOf(bodyReal, StringComparison.Ordinal);
        if (i < 0) return;
        var rewritten = string.Concat(text.AsSpan(0, i), bodyMoniker, text.AsSpan(i + bodyReal.Length));
        try { message.Message = new SeString(new TextPayload(rewritten)); }
        catch (Exception ex) { log.Debug("[HMSync] emote-name rewrite failed: " + ex.Message); }
    }

    // NB-17: pull the player's REAL name out of the sender SeString's PlayerPayload - the true identity, immune to any
    // cosmetic that rewrites the DISPLAYED name (nameplate mods, class-abbrev prefixers, chat-name replacers). Returns
    // null when the sender carries no PlayerPayload, which is the local player's own flat-printed message (own chat is
    // never wrapped in a clickable player link) - the caller treats null as "own message → always show".
    private static string? RealNameFromPayload(Dalamud.Game.Text.SeStringHandling.SeString sender)
    {
        foreach (var p in sender.Payloads)
            if (p is PlayerPayload pp && !string.IsNullOrEmpty(pp.PlayerName))
                return pp.PlayerName;
        return null;
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

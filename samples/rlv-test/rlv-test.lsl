// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
//
// RLV test object: wear it, then send it words on the command channel (from its owner only). Each word sends RLV commands
// to the viewer as owner-say lines starting with '@' (the RestrainedLove API). Replies the viewer sends on the reply
// channel, and lines it redirects to the redirect channel, are said back to the owner as plain owner-say lines, so the
// whole exchange can be followed in chat.
//
// Words on the command channel:
//   lock       @detach=n            this object cannot be taken off
//   unlock     @detach=y
//   notp       @tploc=n,tplm=n      no teleports to a location or a landmark
//   redir      @redirchat:<redirect channel>=add   public chat goes to the redirect channel instead
//   sit <key>  @sit:<key>=force     sit on the object with that key
//   stand      @unsit=force         stand up
//   version    @versionnew=<reply channel>
//   status     @getstatus=<reply channel>
//   clear      @clear               every restriction of this object goes

integer COMMAND_CHANNEL = 7171;
integer REDIRECT_CHANNEL = 7272;
integer REPLY_CHANNEL = 7373;

rlv(string commands)
{
    llOwnerSay("@" + commands);
}

listen_all()
{
    key owner = llGetOwner();
    llListen(COMMAND_CHANNEL, "", owner, "");
    llListen(REDIRECT_CHANNEL, "", owner, "");
    llListen(REPLY_CHANNEL, "", owner, "");
}

default
{
    state_entry()
    {
        listen_all();
    }

    on_rez(integer start)
    {
        llResetScript();
    }

    attach(key wearer)
    {
        if (wearer != NULL_KEY)
            llOwnerSay("RLV test object ready: command channel " + (string)COMMAND_CHANNEL);
    }

    listen(integer channel, string name, key id, string text)
    {
        if (channel == REDIRECT_CHANNEL)
        {
            llOwnerSay("redirected: " + text);
            return;
        }
        if (channel == REPLY_CHANNEL)
        {
            llOwnerSay("reply: " + text);
            return;
        }
        list words = llParseString2List(text, [" "], []);
        string word = llToLower(llList2String(words, 0));
        if (word == "lock") rlv("detach=n");
        else if (word == "unlock") rlv("detach=y");
        else if (word == "notp") rlv("tploc=n,tplm=n");
        else if (word == "redir") rlv("redirchat:" + (string)REDIRECT_CHANNEL + "=add");
        else if (word == "sit") rlv("sit:" + llList2String(words, 1) + "=force");
        else if (word == "stand") rlv("unsit=force");
        else if (word == "version") rlv("versionnew=" + (string)REPLY_CHANNEL);
        else if (word == "status") rlv("getstatus=" + (string)REPLY_CHANNEL);
        else if (word == "clear") rlv("clear");
        else llOwnerSay("unknown word: " + word);
    }
}

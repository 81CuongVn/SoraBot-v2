﻿using System;
using System.Linq;
using System.Threading.Tasks;
using ArgonautCore.Lw;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SoraBot.Common.Extensions.Modules;
using SoraBot.Common.Utils;
using SoraBot.Data.Repositories.Interfaces;
using SoraBot.Services.Cache;

namespace SoraBot.Services.ReactionHandlers
{
    public class StarboardService : IStarboardService
    {
        public const string STAR_EMOTE = "⭐";

        private readonly TimeSpan _messageCacheTtl = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _postedMsgTtl = TimeSpan.FromHours(1);
        private readonly TimeSpan _userRatelimitTtl = TimeSpan.FromMinutes(30);

        private readonly ICacheService _cache;
        private readonly IStarboardRepository _starRepo;
        private readonly ILogger<StarboardService> _log;
        private readonly DiscordSocketClient _client;

        public StarboardService(
            ICacheService cache,
            IStarboardRepository starRepo,
            ILogger<StarboardService> log,
            DiscordSocketClient client)
        {
            _cache = cache;
            _starRepo = starRepo;
            _log = log;
            _client = client;
        }

        private static bool IsStarEmote(IEmote emote)
            => emote.Name == STAR_EMOTE;

        public async Task HandleReactionCleared(Cacheable<IUserMessage, ulong> msg)
        {
            // Just check if the message is a starboard message. If so we remove it and add it to the 
            // don't post again list. Simple and easy

            var starmsg = await _starRepo.GetStarboardMessage(msg.Id).ConfigureAwait(false);
            // This means its not in the DB so we don't care about it essentially
            if (!starmsg.HasValue)
                return;

            var guild = _client.GetGuild(starmsg.Some().GuildId);
            var starboardInfo = await _starRepo.GetStarboardInfo(guild.Id).ConfigureAwait(false);
            if (!starboardInfo.HasValue) return;

            var starboardChannel = guild.GetTextChannel(starboardInfo.Some().starboardChannelId) as ITextChannel;
            if (starboardChannel == null) return;

            await this.RemoveStarboardMessage(msg.Id, starmsg.Some().PostedMsgId, starboardChannel)
                .ConfigureAwait(false);
        }

        public async Task HandleReactionAdded(Cacheable<IUserMessage, ulong> msg, SocketReaction reaction)
        {
            if (!IsStarEmote(reaction.Emote)) return;
            // Abort if its in the "do not post again" cache
            if (_cache.Contains(CacheId.StarboardDoNotPostId(msg.Id))) return;

            // Check if user reached his post ratelimit
            if (this.UserRateLimitReached(msg.Id, reaction.UserId)) return;

            // Try get message
            var message = await TryGetMessageAndValidate(msg, reaction.UserId).ConfigureAwait(false);
            if (message == null) return;

            // Check if this is in a guild and not DMs
            if (!(message.Channel is IGuildChannel channel)) return;
            var guildInfo = await _starRepo.GetStarboardInfo(channel.GuildId).ConfigureAwait(false);
            // This means that either there is no guild in the DB or it has no starboard Channel ID
            if (!guildInfo.HasValue) return;

            // Check if still valid channel and if not remove the values from the DB
            var starboardChannel = await this
                .IsValidChannelAndRemoveIfNot(guildInfo.Some().starboardChannelId, channel.Guild).ConfigureAwait(false);
            if (starboardChannel == null) return;
            // Check threshold
            var reactionCount = await GetReactionCount(message, reaction.Emote).ConfigureAwait(false);
            if (reactionCount < guildInfo.Some().threshold) return;

            // Channel is setup and exists and msg exceed threshold.
            // Check if message is already posted
            if (!await this.TryUpdatePostedMessage(message, starboardChannel, reactionCount).ConfigureAwait(false))
            {
                // Post the message
                var postedMsg = await this.PostAndCacheMessage(message, starboardChannel, reactionCount)
                    .ConfigureAwait(false);
                await _starRepo.AddStarboardMessage(channel.Guild.Id, message.Id, postedMsg.Id).ConfigureAwait(false);
            }

            // We've handled the users Reaction. Let's keep track of it. A user is only allowed to react to a message TWICE
            // This means he can add and remove the star. After that his actions will be ignored
            this.AddOrUpdateRateLimit(msg.Id, reaction.UserId);
        }

        public async Task HandleReactionRemoved(Cacheable<IUserMessage, ulong> msg, SocketReaction reaction)
        {
            if (!IsStarEmote(reaction.Emote)) return;
            // Abort if its in the "do not post again" cache
            if (_cache.Contains(CacheId.StarboardDoNotPostId(msg.Id))) return;

            // Check if user reached his post ratelimit
            if (this.UserRateLimitReached(msg.Id, reaction.UserId)) return;

            // Try get message
            var message = await TryGetMessageAndValidate(msg, reaction.UserId).ConfigureAwait(false);
            if (message == null) return;

            // Check if this is in a guild and not DMs
            if (!(message.Channel is IGuildChannel channel)) return;
            var guildInfo = await _starRepo.GetStarboardInfo(channel.GuildId).ConfigureAwait(false);
            // This means that either there is no guild in the DB or it has no starboard Channel ID
            if (!guildInfo.HasValue) return;

            // Check if still valid channel and if not remove the values from the DB
            var starboardChannel = await this
                .IsValidChannelAndRemoveIfNot(guildInfo.Some().starboardChannelId, channel.Guild).ConfigureAwait(false);
            if (starboardChannel == null) return;

            // Check if still above threshold so we just update the count
            var reactionCount = await GetReactionCount(message, reaction.Emote).ConfigureAwait(false);
            if (reactionCount >= guildInfo.Some().threshold)
            {
                await this.TryUpdatePostedMessage(message, starboardChannel, reactionCount).ConfigureAwait(false);
            }
            // Below threshold so we remove it from the Starboard and add it to the list of
            // never to be added again messages. (at least during runtime)
            else
            {
                var starmsg = await _starRepo.GetStarboardMessage(message.Id).ConfigureAwait(false);
                // This means its not in the DB so we don't care about it essentially
                if (!starmsg.HasValue)
                    return;
                await this.RemoveStarboardMessage(message.Id, starmsg.Some().PostedMsgId, starboardChannel)
                    .ConfigureAwait(false);
            }

            this.AddOrUpdateRateLimit(msg.Id, reaction.UserId);
        }

        private async Task RemoveStarboardMessage(ulong messageId, ulong postedMessageId, ITextChannel starboardChannel)
        {
            // Remove it from DB and Cache :)
            await this.RemoveStarboardMessageFromCacheAndDb(messageId, postedMessageId).ConfigureAwait(false);
            // Physically remove the message now
            var postedMsg = await this.GetStarboardMessage(postedMessageId, starboardChannel).ConfigureAwait(false);
            if (!postedMsg.HasValue) return; // Msg doesn't exist anymore
            try
            {
                await postedMsg.Some().DeleteAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Failed to remove starboard message");
            }

            // Add it to the cache to never be added again
            _cache.Set(CacheId.StarboardDoNotPostId(messageId), null);
        }

        private bool UserRateLimitReached(ulong messageId, ulong userId)
        {
            var reactCount = _cache.Get<int>(CacheId.StarboardUserMessageReactCountId(messageId, userId));
            if (reactCount.HasValue && reactCount.Some() >= 2) return true;
            return false;
        }

        private void AddOrUpdateRateLimit(ulong messageId, ulong userId)
        {
            _cache.AddOrUpdate(CacheId.StarboardUserMessageReactCountId(messageId, userId),
                new CacheItem(1, _userRatelimitTtl), (id, item) =>
                {
                    int amount = (int) item.Content;
                    return new CacheItem(amount + 1, _userRatelimitTtl); // Let's refresh the TTL on update
                });
        }

        private async Task<IUserMessage> TryGetMessageAndValidate(Cacheable<IUserMessage, ulong> msg,
            ulong reactionUserId)
        {
            var messageM = await this.GetOrDownloadMessage(msg).ConfigureAwait(false);
            if (!messageM.HasValue) return null;
            if (messageM.Some().Author.IsBot || messageM.Some().Author.IsWebhook) return null;
            if (reactionUserId == messageM.Some().Author.Id) return null;

            return messageM.Some();
        }

        private async Task<bool> TryUpdatePostedMessage(IUserMessage message, ITextChannel starboardChannel,
            int reactionCount)
        {
            var starmsg = await _starRepo.GetStarboardMessage(message.Id).ConfigureAwait(false);
            if (!starmsg.HasValue)
                return false;

            // Check if message still exists
            var starMessage = await this.GetStarboardMessage(starmsg.Some().PostedMsgId, starboardChannel)
                .ConfigureAwait(false);
            if (starMessage.HasValue)
            {
                // Update message
                await starMessage.Some()
                    .ModifyAsync(x => { x.Content = $"**{reactionCount.ToString()}** {STAR_EMOTE}"; })
                    .ConfigureAwait(false);
            }
            else
            {
                // Remove the message from the cache and from the repo
                await this.RemoveStarboardMessageFromCacheAndDb(starmsg.Some().MessageId, starmsg.Some().PostedMsgId)
                    .ConfigureAwait(false);
            }

            return true;
        }

        private async Task<IUserMessage> PostAndCacheMessage(IUserMessage msg, ITextChannel starboardChannel,
            int reactionCount)
        {
            var eb = new EmbedBuilder()
            {
                Color = SoraSocketCommandModule.Purple,
                Author = new EmbedAuthorBuilder()
                {
                    IconUrl = msg.Author.GetAvatarUrl() ?? msg.Author.GetDefaultAvatarUrl(),
                    Name = Formatter.UsernameDiscrim(msg.Author)
                }
            };
            if (!TryAddImageAttachment(msg, eb)) // First check if there's an attached image
                if (!TryAddImageLink(msg, eb)) // Check if there's an image link
                    TryAddArticleThumbnail(msg, eb); // Is it a link?

            // Otherwise make a normal embed
            if (!string.IsNullOrWhiteSpace(msg.Content))
                eb.WithDescription(msg.Content);

            eb.AddField("Posted in", $"[#{msg.Channel.Name} (take me!)]({msg.GetJumpUrl()})");
            eb.WithTimestamp(msg.Timestamp);

            var postedMsg = await starboardChannel
                .SendMessageAsync($"**{reactionCount.ToString()}** {STAR_EMOTE}", embed: eb.Build())
                .ConfigureAwait(false);

            _cache.Set(CacheId.GetMessageId(postedMsg.Id), postedMsg, _postedMsgTtl);
            return postedMsg;
        }

        private static void TryAddArticleThumbnail(IUserMessage msg, EmbedBuilder eb)
        {
            var thumbnail = msg.Embeds.Select(x => x.Thumbnail).FirstOrDefault(x => x.HasValue);
            if (!thumbnail.HasValue) return;
            eb.WithImageUrl(thumbnail.Value.Url);
        }

        private static bool TryAddImageLink(IUserMessage msg, EmbedBuilder eb)
        {
            var imageEmbed = msg.Embeds.Select(x => x.Image).FirstOrDefault(x => x.HasValue);
            if (!imageEmbed.HasValue) return false;
            eb.WithImageUrl(imageEmbed.Value.Url);
            return true;
        }

        private static bool TryAddImageAttachment(IUserMessage msg, EmbedBuilder eb)
        {
            if (msg.Attachments.Count == 0) return false;
            var image = msg.Attachments.FirstOrDefault(x => !Helper.LinkIsNoImage(x.Url));
            if (image == null) return false;
            eb.WithImageUrl(image.Url);
            return true;
        }

        private async Task RemoveStarboardMessageFromCacheAndDb(ulong messageId, ulong postedMessageId)
        {
            _cache.TryRemove<object>(CacheId.GetMessageId(messageId));
            _cache.TryRemove<object>(CacheId.GetMessageId(postedMessageId));
            await _starRepo.RemoveStarboardMessage(messageId).ConfigureAwait(false);
        }

        private async Task<Option<IUserMessage>> GetStarboardMessage(ulong messageId, ITextChannel starboardChannel)
        {
            return await _cache.TryGetOrSetAndGetAsync(
                CacheId.GetMessageId(messageId),
                async () => await starboardChannel.GetMessageAsync(messageId, CacheMode.AllowDownload)
                    .ConfigureAwait(false) as IUserMessage,
                _postedMsgTtl).ConfigureAwait(false);
        }

        private async Task<int> GetReactionCount(IUserMessage msg, IEmote emote)
        {
            var reactions = await msg.GetReactionUsersAsync(emote, 100).FlattenAsync().ConfigureAwait(false);
            return reactions.Count(u => u.Id != msg.Author.Id);
        }

        private async Task<ITextChannel> IsValidChannelAndRemoveIfNot(ulong channelId, IGuild guild)
        {
            var channel = await guild.GetTextChannelAsync(channelId, CacheMode.AllowDownload).ConfigureAwait(false);
            if (channel != null) return channel;
            // Otherwise get rid of outdated info in DB
            await _starRepo.RemoveStarboard(guild.Id).ConfigureAwait(false);
            return null;
        }

        private async Task<Option<IUserMessage>> GetOrDownloadMessage(Cacheable<IUserMessage, ulong> msg)
            => await _cache.TryGetOrSetAndGetAsync(
                    CacheId.GetMessageId(msg.Id),
                    async () => await msg.GetOrDownloadAsync().ConfigureAwait(false),
                    this._messageCacheTtl)
                .ConfigureAwait(false);
    }
}
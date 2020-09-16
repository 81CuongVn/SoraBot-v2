﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArgonautCore.Lw;
using Microsoft.EntityFrameworkCore;
using SoraBot.Data.Extensions;
using SoraBot.Data.Models.SoraDb;
using SoraBot.Data.Repositories.Interfaces;

namespace SoraBot.Data.Repositories
{
    public class MarriageRepository : IMarriageRepository
    {
        private readonly ITransactor<SoraContext> _soraTransactor;

        public MarriageRepository(ITransactor<SoraContext> soraTransactor)
        {
            _soraTransactor = soraTransactor;
        }

        public async Task<Option<List<Marriage>>> GetAllMarriagesOfUser(ulong userId)
            => await _soraTransactor.DoAsync(async context =>
            {
                var marriages = await context.Marriages.Where(x => x.Partner1 == userId || x.Partner2 == userId)
                    .ToListAsync().ConfigureAwait(false);

                if (marriages.Count == 0)
                    return Option.None<List<Marriage>>();
                
                return marriages;
            }).ConfigureAwait(false);

        public async Task<bool> TryAddMarriage(ulong user1, ulong user2)
            => await _soraTransactor.TryDoInTransactionAsync(async context =>
            {
                this.OrderUserIdsRef(ref user1, ref user2);
                
                // Check if already exists
                var marr = await context.Marriages.FirstOrDefaultAsync(x => x.Partner1 == user1 && x.Partner2 == user2);
                if (marr != null)
                    return false;
                
                // Make sure users exists :)
                await context.Users.GetOrCreateUserNoSaveAsync(user1);
                await context.Users.GetOrCreateUserNoSaveAsync(user2);
                
                // Otherwise we create it
                var marriage = new Marriage()
                {
                    Partner1 = user1,
                    Partner2 = user2,
                    PartnerSince = DateTime.UtcNow
                };

                context.Marriages.Add(marriage);
                await context.SaveChangesAsync();
                return true;
            }).ConfigureAwait(false);

        public async Task<bool> TryDivorce(ulong user1, ulong user2)
            => await _soraTransactor.TryDoInTransactionAsync(async context =>
            {
                // Sort ids
                this.OrderUserIdsRef(ref user1, ref user2);
                // Check if marriage exists
                var marr = await context.Marriages.FirstOrDefaultAsync(x => x.Partner1 == user1 && x.Partner2 == user2);
                if (marr == null)
                    return false;
                
                // remove it
                context.Marriages.Remove(marr);
                await context.SaveChangesAsync();
                return true;
            }).ConfigureAwait(false);

        public async Task<int> GetUserMarriageCount(ulong userId)
            => await _soraTransactor.DoAsync(async context => 
                await context.Marriages
                    .CountAsync(x => x.Partner1 == userId || x.Partner2 == userId)
            ).ConfigureAwait(false);

        private void OrderUserIdsRef(ref ulong user1, ref ulong user2)
        {
            if (user1 < user2)
                return;
            
            var temp = user1;
            user1 = user2;
            user2 = temp;
        }
    }
}
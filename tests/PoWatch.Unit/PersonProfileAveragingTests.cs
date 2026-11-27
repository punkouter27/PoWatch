using PoWatch.Application.Contracts;
using PoWatch.Application.Features.Regulars;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Unit;

/// <summary>A person's Beauty and Age on the People page are the mean of every reading saved for them.</summary>
public sealed class PersonProfileAveragingTests
{
    private const string User = "u1";
    private static readonly List<float> Me = [1f, 0f, 0f, 0f];
    private static readonly List<float> Other = [0f, 1f, 0f, 0f];

    [Fact]
    public async Task Beauty_and_age_are_the_mean_of_every_reading()
    {
        var service = new PersonProfileService(new MemoryStore(), TimeProvider.System);
        await service.ObserveAsync(User, Reading(Me, beauty: 40, age: 30), default);
        await service.ObserveAsync(User, Reading(Me, beauty: 50, age: 40), default);
        await service.ObserveAsync(User, Reading(Me, beauty: 60, age: 50), default);

        var person = Assert.Single(await service.ListAsync(User, default));
        Assert.Equal(50, person.Stats.BeautyMean);
        Assert.Equal(40, person.Stats.AgeMean);
        Assert.Equal(40, person.Stats.BeautyLow);
        Assert.Equal(60, person.Stats.BeautyHigh);
    }

    [Fact]
    public async Task A_reading_without_a_value_does_not_pull_its_mean_down()
    {
        var service = new PersonProfileService(new MemoryStore(), TimeProvider.System);
        await service.ObserveAsync(User, new ObservePersonRequestDto
        {
            SessionId = Guid.NewGuid(),
            Descriptor = Me,
            Beauty = 60,
            AgeYears = 30,
            Mood = 80,
            Emotion = "happy",
            HeartRateBpm = 70,
            Golden = 0.9,
            Thirds = 0.7
        }, default);
        await service.ObserveAsync(User, new ObservePersonRequestDto
        {
            SessionId = Guid.NewGuid(),
            Descriptor = Me,
            AgeYears = 50,
            Mood = 40,
            Emotion = "neutral"
        }, default);

        var person = Assert.Single(await service.ListAsync(User, default));
        Assert.Equal(60, person.Stats.BeautyMean);
        Assert.Equal(40, person.Stats.AgeMean);
        Assert.Equal(60, person.Stats.MoodMean);
        Assert.Equal("neutral", person.Stats.Emotion);
        Assert.Equal(70, person.Stats.HeartRateMean);
        Assert.Equal(0.8, person.Stats.ProportionsMean);
    }

    [Fact]
    public async Task Merging_keeps_the_mean_over_both_peoples_readings()
    {
        var service = new PersonProfileService(new MemoryStore(), TimeProvider.System);
        var a = (await service.ObserveAsync(User, Reading(Me, beauty: 30, age: 20), default)).Person;
        var b = (await service.ObserveAsync(User, Reading(Other, beauty: 60, age: 50), default)).Person;
        await service.ObserveAsync(User, Reading(Other, beauty: 90, age: 50), default);

        var merged = await service.MergeAsync(User, a.Id, b.Id, default);

        Assert.NotNull(merged);
        Assert.Equal(60, merged.Stats.BeautyMean);   // (30 + 60 + 90) / 3
        Assert.Equal(40, merged.Stats.AgeMean);      // (20 + 50 + 50) / 3
    }

    [Fact]
    public void A_profile_saved_before_BeautyCount_existed_still_averages_by_its_face_samples()
    {
        var legacy = new PersonProfile
        {
            Id = "p-old",
            UserId = User,
            Number = 1,
            Descriptor = [1f, 0f, 0f, 0f],
            Stats = new PersonProfileStats { FaceSamples = 4, BeautySum = 200 }
        };
        Assert.Equal(50, PersonProfileService.ToDto(legacy).Stats.BeautyMean);
    }

    private static ObservePersonRequestDto Reading(List<float> descriptor, double? beauty, double? age) => new()
    {
        SessionId = Guid.NewGuid(),
        Descriptor = descriptor,
        Beauty = beauty,
        AgeYears = age
    };

    private sealed class MemoryStore : IPersonProfileStore
    {
        private readonly Dictionary<string, PersonProfile> _rows = [];

        public Task<IReadOnlyList<PersonProfile>> ListAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersonProfile>>([.. _rows.Values.Where(p => p.UserId == userId)]);

        public Task<PersonProfile?> GetAsync(string userId, string personId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(personId, out var p) && p.UserId == userId ? p : null);

        public Task UpsertAsync(PersonProfile profile, CancellationToken cancellationToken)
        {
            _rows[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string userId, string personId, CancellationToken cancellationToken)
        {
            _rows.Remove(personId);
            return Task.CompletedTask;
        }
    }
}

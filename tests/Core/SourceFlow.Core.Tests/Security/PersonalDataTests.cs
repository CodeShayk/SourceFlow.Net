using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Core.Tests.Runtime;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Security;

/// <summary>UT-PII-01..18 (plan 05, WP-5.2): crypto-shredding of <c>[PersonalData]</c> members (doc 10 §4).</summary>
[Trait("Category", "Unit")]
public sealed class PersonalDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-PII-01")]
    [Trait("Covers", "doc 10 4")]
    public async Task PersonalDataMembers_AreStoredEncrypted_OthersInTheClear()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var id = PatientId.New();

        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);

        string stored = await RawAsync(host, id);
        stored.ShouldNotContain("Ada Lovelace");
        stored.ShouldNotContain("12 Byron Lane");
        stored.ShouldContain("\"$pd\":\"v1:");
        stored.ShouldContain("\"city\":\"London\"", customMessage: "the city is not personal data");
        stored.ShouldContain("\"customer\":\"cust-1\"", customMessage: "the subject id itself is not encrypted");
    }

    [Fact]
    [Trait("Id", "UT-PII-02")]
    [Trait("Covers", "doc 10 4")]
    public async Task Reads_DecryptTransparently_ForLoadsProjectionsAndQueries()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var id = PatientId.New();
        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);

        await host.SendAsync(new RenamePatient(id, "Ada King"), cancellationToken: Ct);   // the load decrypts state
        await host.WaitForIdleAsync();

        IReadOnlyList<IEvent> events = await host.EventsAsync(StreamFor(id));
        ((PatientRegistered)events[0]).Name.ShouldBe("Ada Lovelace");
        ((PatientRegistered)events[0]).Age.ShouldBe(36);
        ((PatientRegistered)events[0]).Home.Street.ShouldBe("12 Byron Lane");
        (await host.DocumentAsync<PatientCardsProjection, PatientCard>(id.ToStreamSegment()))!.Name.ShouldBe("Ada King");
        AggregateSnapshot<Patient> state = await host.Services.GetRequiredService<SourceFlow.Query.IAggregateReader>().LoadAsync<Patient, PatientId>(id, cancellationToken: Ct);
        state.State.Name.ShouldBe("Ada King");
    }

    [Fact]
    [Trait("Id", "UT-PII-03")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erase_MakesEveryMemberOfTheSubjectReadAsErased()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var id = PatientId.New();
        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);

        ErasureResult result = await Eraser(host).EraseSubjectAsync("cust-1", cancellationToken: Ct);

        result.Streams.ShouldBe(1);
        var registered = (PatientRegistered)(await host.EventsAsync(StreamFor(id)))[0];
        registered.Name.ShouldBe(PersonalDataProtector.Erased);
        registered.Age.ShouldBe(0);
        registered.Home.Street.ShouldBe(PersonalDataProtector.Erased);
        registered.Home.City.ShouldBe("London", "not personal data: kept");
        registered.Customer.ShouldBe("cust-1");
    }

    [Fact]
    [Trait("Id", "UT-PII-04")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erase_AppendsThePersonalDataErasedEvent()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        await host.SendAsync(Register(PatientId.New(), "cust-9"), cancellationToken: Ct);

        await Eraser(host).EraseSubjectAsync("cust-9", cancellationToken: Ct);

        IEvent erased = (await host.EventsAsync(new StreamId("personal-data-cust-9"))).ShouldHaveSingleItem();
        erased.ShouldBe(new PersonalDataErased("cust-9"));
        host.Runtime.Model.Registry.Get(typeof(PersonalDataErased)).Name.ShouldBe(PersonalDataErased.TypeName);
    }

    [Fact]
    [Trait("Id", "UT-PII-05")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erase_DeletesSnapshots_AndEvictsCachedStates()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, sf => sf.Configure(o => o.AggregateCache = AggregateCacheMode.ReadTail));
        var id = PatientId.New();
        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);
        await host.SendAsync(new RenamePatient(id, "Ada King"), cancellationToken: Ct);   // SnapshotEvery = 2
        await host.WaitForIdleAsync();
        Snapshot snapshot = (await ((ISnapshotStore)host.Store).GetAsync("-", StreamFor(id), Ct)).ShouldNotBeNull();
        Encoding.UTF8.GetString(snapshot.Data.Span).ShouldNotContain("Ada King", customMessage: "snapshots encrypt [PersonalData] state too");

        await Eraser(host).EraseSubjectAsync("cust-1", cancellationToken: Ct);

        (await ((ISnapshotStore)host.Store).GetAsync("-", StreamFor(id), Ct)).ShouldBeNull();
        host.Runtime.AggregateCache.TryGet("-", StreamFor(id), typeof(Patient), out _, out _).ShouldBeFalse();
        AggregateSnapshot<Patient> state = await host.Services.GetRequiredService<SourceFlow.Query.IAggregateReader>().LoadAsync<Patient, PatientId>(id, cancellationToken: Ct);
        state.State.Name.ShouldBe(PersonalDataProtector.Erased);
    }

    [Fact]
    [Trait("Id", "UT-PII-06")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erase_ScrubsPersonalDataInDocuments()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var id = PatientId.New();
        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        ErasureResult result = await Eraser(host).EraseSubjectAsync("cust-1", cancellationToken: Ct);

        result.DocumentsScrubbed.ShouldBe(1);
        PatientCard card = (await host.DocumentAsync<PatientCardsProjection, PatientCard>(id.ToStreamSegment()))!;
        card.Name.ShouldBe(PersonalDataProtector.Erased);
        card.City.ShouldBe("London");
    }

    [Fact]
    [Trait("Id", "UT-PII-07")]
    public async Task Erase_LeavesOtherSubjectsAlone()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var erased = PatientId.New();
        var kept = PatientId.New();
        await host.SendAsync(Register(erased, "cust-1"), cancellationToken: Ct);
        await host.SendAsync(Register(kept, "cust-2"), cancellationToken: Ct);

        await Eraser(host).EraseSubjectAsync("cust-1", cancellationToken: Ct);

        ((PatientRegistered)(await host.EventsAsync(StreamFor(kept)))[0]).Name.ShouldBe("Ada Lovelace");
    }

    [Fact]
    [Trait("Id", "UT-PII-08")]
    public async Task TamperedOrMovedMarkers_FailDecryption()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        PersonalDataProtector protector = host.Services.GetRequiredService<PersonalDataProtector>();
        var id = PatientId.New();
        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);
        using JsonDocument stored = JsonDocument.Parse(await RawAsync(host, id));
        string nameMarker = stored.RootElement.GetProperty("name").GetProperty("$pd").GetString()!;

        // The same ciphertext under another member: the associated data names the path.
        byte[] moved = Encoding.UTF8.GetBytes($"{{\"street\":{{\"$pd\":\"{nameMarker}\"}}}}");
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(moved, Ct));
        byte[] garbled = Encoding.UTF8.GetBytes("{\"name\":{\"$pd\":\"v1:x\"}}");
        await Should.ThrowAsync<DecryptionFailedException>(async () => await protector.UnprotectAsync(garbled, Ct));
    }

    [Fact]
    [Trait("Id", "UT-PII-09")]
    [Trait("Covers", "doc 10 4")]
    public async Task WritesAfterErasure_KeepNothingNew()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        await Eraser(host).EraseSubjectAsync("cust-gone", cancellationToken: Ct);
        var id = PatientId.New();

        await host.SendAsync(Register(id, "cust-gone"), cancellationToken: Ct);

        string stored = await RawAsync(host, id);
        stored.ShouldNotContain("$pd");
        stored.ShouldContain("\"name\":\"[erased]\"");
        stored.ShouldContain("\"age\":0");
        keys.Generated.ShouldBe(0, "no key is made for an erased subject");
    }

    [Fact]
    [Trait("Id", "UT-PII-10")]
    public async Task DataKeys_AreMadeOncePerSubject_AndCached()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(Register(PatientId.New(), "cust-1"), cancellationToken: Ct);
        }

        await host.SendAsync(Register(PatientId.New(), "cust-2"), cancellationToken: Ct);

        keys.Generated.ShouldBe(2);
        keys.Unwrapped.ShouldBe(2, "each key is unwrapped once, then cached");
        (await host.Store.GetKeyAsync("cust-1", Ct)).ShouldNotBeNull().KeyReference.ShouldBe("local-master/v1");
    }

    [Fact]
    [Trait("Id", "UT-PII-11")]
    public async Task WithoutEncryption_PersonalDataIsStoredAsIs()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<CoreTestModule>();
        }, cancellationToken: Ct);
        var id = PatientId.New();

        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);

        (await RawAsync(host, id)).ShouldContain("Ada Lovelace");
        host.Services.GetService<IPersonalDataEraser>().ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-PII-12")]
    public async Task Encryption_WithoutAKeyProvider_FailsAtStartup()
    {
        InvalidOperationException e = await Should.ThrowAsync<InvalidOperationException>(async () => await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<CoreTestModule>();
            sf.Security(s => s.EncryptPersonalData());
        }, cancellationToken: Ct));

        e.Message.ShouldContain("key provider");
    }

    [Fact]
    [Trait("Id", "UT-PII-13")]
    [Trait("Covers", "doc 10 4")]
    public async Task SubjectStreams_AreRecorded_ForEveryStreamHoldingTheSubjectsData()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);
        var first = PatientId.New();
        var second = PatientId.New();
        await host.SendAsync(Register(first, "cust-1"), cancellationToken: Ct);
        await host.SendAsync(Register(second, "cust-1"), cancellationToken: Ct);

        IReadOnlyList<StreamId> streams = await host.Store.GetSubjectStreamsAsync("-", "cust-1", Ct);

        streams.ShouldBe([StreamFor(first), StreamFor(second)], ignoreOrder: true);
    }

    [Fact]
    [Trait("Id", "UT-PII-14")]
    [Trait("Covers", "doc 10 4")]
    public async Task AsyncProjections_SeeDecryptedValues()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys, sf => sf.Projections(p => p.Add<PatientCardsProjection>().Async()));
        var id = PatientId.New();

        await host.SendAsync(Register(id, "cust-1"), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        (await host.DocumentAsync<PatientCardsProjection, PatientCard>(id.ToStreamSegment()))!.Name.ShouldBe("Ada Lovelace");
    }

    [Fact]
    [Trait("Id", "UT-PII-15")]
    public void UnresolvableSubject_IsRejected()
    {
        using var keys = new LocalKeyProvider();
        var model = new SourceFlowModelBuilder().AddModule<CoreTestModule>().AddModule<PatientCardsModule>().Build();
        using var protector = new PersonalDataProtector(keys, new InMemoryStore(), model);

        protector.Protects(typeof(PatientRegistered)).ShouldBeTrue();
        protector.Protects(typeof(PatientId)).ShouldBeFalse();
        protector.Plan(typeof(PatientRegistered)).Members.Select(m => string.Join('.', m.Path)).ShouldBe(["name", "age", "home.street"], ignoreOrder: true);
        protector.Plan(typeof(PatientRegistered)).Members.ShouldAllBe(m => m.SubjectPath != null && m.SubjectPath.SequenceEqual(new[] { "customer" }));
    }

    [Fact]
    [Trait("Id", "UT-PII-16")]
    public async Task WithoutASubjectMember_TheStreamIsTheSubject()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<Bank.Accounts.AccountsModule>();
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
        }, cancellationToken: Ct);
        var account = Bank.Accounts.AccountId.New();
        await host.SendAsync(new Bank.Accounts.OpenAccount(account, "Ada", 5m), cancellationToken: Ct);
        StreamId stream = StreamId.For(Bank.Accounts.BankAccount.AggregateName, account);

        await host.Services.GetRequiredService<IPersonalDataEraser>().EraseSubjectAsync(stream.Value, cancellationToken: Ct);

        ((Bank.Accounts.AccountOpened)(await host.EventsAsync(stream))[0]).Holder.ShouldBe(PersonalDataProtector.Erased);
    }

    [Fact]
    [Trait("Id", "UT-PII-17")]
    public async Task UnknownSubject_ReadsAsErased()
    {
        using var keys = new LocalKeyProvider();
        var model = new SourceFlowModelBuilder().AddModule<CoreTestModule>().Build();
        var store = new InMemoryStore();
        using var protector = new PersonalDataProtector(keys, store, model);
        (ReadOnlyMemory<byte> body, _) = await protector.ProtectAsync(typeof(PatientRenamed), "s", Encoding.UTF8.GetBytes("{\"patientId\":\"x\",\"customer\":\"c\",\"name\":\"Ada\"}"), Ct);

        using var empty = new PersonalDataProtector(keys, new InMemoryStore(), model);   // a store that never saw the key

        Encoding.UTF8.GetString((await empty.UnprotectAsync(body, Ct)).Span).ShouldContain("\"name\":\"[erased]\"");
        Encoding.UTF8.GetString((await protector.UnprotectAsync(body, Ct)).Span).ShouldContain("\"name\":\"Ada\"");
    }

    [Fact]
    [Trait("Id", "UT-PII-18")]
    [Trait("Covers", "doc 14 8")]
    public async Task Sfql_RejectsFiltersOnPersonalData()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await HostAsync(keys);

        SourceFlow.Query.QueryException e = await Should.ThrowAsync<SourceFlow.Query.QueryException>(async () =>
            await host.Services.GetRequiredService<SourceFlow.Query.IQueryService>().QueryPageAsync("from readmodel \"test-patient-cards\" | where name = 'Ada'", cancellationToken: Ct));

        e.Code.ShouldBe("SFQ0301");
    }

    [Fact]
    [Trait("Id", "UT-PII-24")]
    [Trait("Covers", "doc 10 4")]
    public async Task InMemoryKeyStore_Conformance()
    {
        await SourceFlow.Testing.Conformance.PersonalDataKeyStoreConformance.RunAsync(new InMemoryStore(), TimeProvider.System.GetUtcNow(), Ct);
    }

    // ================================================================ helpers

    private static RegisterPatient Register(PatientId id, string customer)
    {
        return new RegisterPatient(id, customer, "Ada Lovelace", 36, new PatientAddress("12 Byron Lane", "London"));
    }

    private static StreamId StreamFor(PatientId id)
    {
        return StreamId.For(Patient.AggregateName, id);
    }

    private static IPersonalDataEraser Eraser(SourceFlowTestHost host)
    {
        return host.Services.GetRequiredService<IPersonalDataEraser>();
    }

    private static async Task<string> RawAsync(SourceFlowTestHost host, PatientId id)
    {
        StreamSlice slice = await ((IEventStore)host.Store).ReadStreamAsync("-", StreamFor(id), new StreamVersion(1), 1, Ct);
        return Encoding.UTF8.GetString(slice.Events[0].Data.Span);
    }

    private static Task<SourceFlowTestHost> HostAsync(LocalKeyProvider keys, Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<CoreTestModule>();
            sf.AddModule<PatientCardsModule>();
            sf.Projections(p => p.Add<PatientCardsProjection>().Inline());
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
            configure?.Invoke(sf);
        }, cancellationToken: Ct);
    }
}

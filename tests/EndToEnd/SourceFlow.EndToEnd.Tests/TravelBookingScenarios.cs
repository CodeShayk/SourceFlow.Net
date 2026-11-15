using Bookings;
using Hotels;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Workflows;
using BookingId = Bookings.BookingId;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// The TravelBooking sample's trip-booking workflow between its two services (TB-E2E-01..06): the booking confirmed, the
/// hotel sold out, the guest not confirming in time, the guest cancelling, a hold released after the point of no return
/// (manual intervention and an operator's abort), and an outage of the Hotels service ridden out by the step's retries.
/// </summary>
/// <remarks>
/// TB-E2E-01 runs on PostgreSQL over every transport; TB-E2E-02..06 on the in-memory store and on PostgreSQL (in-memory
/// broker). The cloud legs move to the real cloud with <c>SOURCEFLOW_REAL_CLOUD</c> (nightly).
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Category", "Workflow")]
public sealed class TravelBookingScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    private const string Requested = "bookings.booking-requested.v1";
    private const string GuestConfirmed = "bookings.guest-confirmed.v1";
    private const string GuestCancelled = "bookings.guest-cancelled.v1";
    private const string Confirmed = "bookings.booking-confirmed.v1";
    private const string Declined = "bookings.booking-declined.v1";
    private const string Registered = "hotels.hotel-registered.v1";
    private const string Held = "hotels.room-reserved.v1";
    private const string Released = "hotels.room-released.v1";
    private const string Reserved = "hotels.reservation-confirmed.v1";

    // The Bookings clock only has to pass a timer's due time; a second more avoids an edge.
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private Servers Servers => new(postgreSql, localStack, pubSub, serviceBus);

    // ================================================================ TB-E2E-01: the booking is confirmed

    [Fact]
    [Trait("Id", "TB-E2E-01")]
    [Trait("Requires", "Docker")]
    public Task Booking_IsConfirmed_InMemoryBroker() => ConfirmedAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-01")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task Booking_IsConfirmed_Aws() => ConfirmedAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "TB-E2E-01")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task Booking_IsConfirmed_Gcp() => ConfirmedAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "TB-E2E-01")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task Booking_IsConfirmed_Azure() => ConfirmedAsync(Broker.Azure);

    // ================================================================ TB-E2E-02..06 on both stores

    [Fact]
    [Trait("Id", "TB-E2E-02")]
    public Task SoldOut_DeclinesTheBooking_InMemory() => SoldOutAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-02")]
    [Trait("Requires", "Docker")]
    public Task SoldOut_DeclinesTheBooking_PostgreSql() => SoldOutAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "TB-E2E-03")]
    public Task GuestTooLate_ReleasesTheRoom_InMemory() => GuestTooLateAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-03")]
    [Trait("Requires", "Docker")]
    public Task GuestTooLate_ReleasesTheRoom_PostgreSql() => GuestTooLateAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "TB-E2E-04")]
    public Task GuestCancels_ReleasesTheRoom_InMemory() => GuestCancelsAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-04")]
    [Trait("Requires", "Docker")]
    public Task GuestCancels_ReleasesTheRoom_PostgreSql() => GuestCancelsAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "TB-E2E-05")]
    public Task HoldLostAfterThePivot_NeedsAnOperator_InMemory() => HoldLostAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-05")]
    [Trait("Requires", "Docker")]
    public Task HoldLostAfterThePivot_NeedsAnOperator_PostgreSql() => HoldLostAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "TB-E2E-06")]
    public Task HotelsOutage_IsRiddenOutByRetries_InMemory() => HotelsOutageAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "TB-E2E-06")]
    [Trait("Requires", "Docker")]
    public Task HotelsOutage_IsRiddenOutByRetries_PostgreSql() => HotelsOutageAsync(Store.PostgreSql);

    // ================================================================ scenarios

    // Hold the room, the guest confirms, the hotel confirms: the booking is confirmed and the room is booked.
    private async Task ConfirmedAsync(Broker broker)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, Store.PostgreSql, broker);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        BookingId booking = await system.RequestAsync(hotel);

        await system.WaitAsync(booking, s => s.Step == "await-guest", "the room to be held and the workflow to wait for the guest");
        await system.WaitForRoomsAsync(hotel, held: 1, confirmed: 0);
        await system.GuestAsync(new ConfirmByGuest(booking));

        WorkflowInstanceSummary done = await system.WaitAsync(booking, s => s.Status == "Completed", "the booking to be confirmed");
        done.PendingCompensations.ShouldBe(1, "hold-room pushed its compensation, which never ran");
        await system.WaitForBookingEventsAsync(booking, Requested, GuestConfirmed, Confirmed);
        await system.WaitForRoomsAsync(hotel, held: 0, confirmed: 1);
        (await system.HotelEventsAsync(hotel)).ShouldBe([Registered, Held, Reserved]);
    }

    // The only room is held by the first booking: the second is rejected by the hotel, and declined without undoing anything.
    private async Task SoldOutAsync(Store store)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, store, Broker.InMemory);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        BookingId first = await system.RequestAsync(hotel);
        await system.WaitAsync(first, s => s.Step == "await-guest", "the first booking to hold the room");

        BookingId second = await system.RequestAsync(hotel);

        WorkflowInstanceSummary declined = await system.WaitAsync(second, s => s.Status == "Compensated", "the second booking to be declined");
        declined.FailureCode.ShouldBe(HotelErrors.SoldOut);
        await system.WaitForBookingEventsAsync(second, Requested, Declined);
        await system.WaitForRoomsAsync(hotel, held: 1, confirmed: 0);
        (await system.SummaryAsync(first))!.Status.ShouldBe("Running", "the first booking still waits for its guest");
    }

    // The guest doesn't confirm within 15 minutes: the hold is released (compensation) and the booking declined.
    private async Task GuestTooLateAsync(Store store)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, store, Broker.InMemory);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        BookingId booking = await system.RequestAsync(hotel);
        await system.WaitAsync(booking, s => s.Step == "await-guest", "the room to be held");

        system.Advance(TimeSpan.FromMinutes(15) + Second);

        await system.WaitAsync(booking, s => s.Status == "Compensated", "the lapsed hold to be undone");
        await system.WaitForBookingEventsAsync(booking, Requested, Declined);
        await system.WaitForRoomsAsync(hotel, held: 0, confirmed: 0);
        (await system.HotelEventsAsync(hotel)).ShouldBe([Registered, Held, Released]);
    }

    // The guest cancels: a failure event of the await-guest step. The hold is released; the booking stays cancelled.
    private async Task GuestCancelsAsync(Store store)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, store, Broker.InMemory);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        BookingId booking = await system.RequestAsync(hotel);
        await system.WaitAsync(booking, s => s.Step == "await-guest", "the room to be held");

        await system.GuestAsync(new CancelByGuest(booking));

        await system.WaitAsync(booking, s => s.Status == "Compensated", "the cancelled booking's hold to be released");
        await system.WaitForRoomsAsync(hotel, held: 0, confirmed: 0);
        (await system.BookingEventsAsync(booking)).ShouldBe([Requested, GuestCancelled], "a cancelled booking is not declined as well");
    }

    // Hotel staff release the hold after the guest confirmed: the confirmation is past the point of no return, so the
    // rejection stops the instance for an operator, who aborts it after rebooking the guest by other means.
    private async Task HoldLostAsync(Store store)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, store, Broker.InMemory);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        BookingId booking = await system.RequestAsync(hotel);
        await system.WaitAsync(booking, s => s.Step == "await-guest", "the room to be held");
        await system.StaffAsync(new ReleaseRoom(hotel, new Hotels.BookingId(booking.Value)));
        await system.WaitForRoomsAsync(hotel, held: 0, confirmed: 0);

        await system.GuestAsync(new ConfirmByGuest(booking));

        WorkflowInstanceSummary manual = await system.WaitAsync(booking, s => s.Status == "ManualIntervention", "the instance to need an operator");
        manual.FailureCode.ShouldBe(HotelErrors.HoldLost);
        (await system.BookingEventsAsync(booking)).ShouldBe([Requested, GuestConfirmed], "the booking stays open for the operator");

        (int code, string output, string error) = await system.CliAsync("workflow", "show", TravelBookingSystem.Workflow, booking.ToString());
        code.ShouldBe(0, error);
        output.ShouldContain("ManualIntervention");

        (code, _, error) = await system.CliAsync("workflow", "abort", TravelBookingSystem.Workflow, booking.ToString(), "--reason", "rebooked by phone");
        code.ShouldBe(0, error);
        (await system.WaitAsync(booking, s => s.Status == "Aborted", "the operator's abort")).Status.ShouldBe("Aborted");
    }

    // Hotels loses what it receives for a while: the hold request times out and is sent again; once Hotels is back, the
    // retry succeeds and the booking completes.
    private async Task HotelsOutageAsync(Store store)
    {
        await using TravelBookingSystem system = await TravelBookingSystem.StartAsync(Servers, store, Broker.InMemory);
        HotelId hotel = await system.RegisterHotelAsync(rooms: 1);
        system.Outage();

        BookingId booking = await system.RequestAsync(hotel);
        await system.WaitAsync(booking, s => s.Step == "hold-room", "the hold to be requested while Hotels is down");
        await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, TestContext.Current.CancellationToken);   // the request is lost
        system.Outage(down: false);
        system.Advance(TimeSpan.FromMinutes(1) + Second);   // the step times out and sends the hold again

        await system.WaitAsync(booking, s => s.Step == "await-guest", "the retried hold to succeed");
        (await TravelBookingSystem.ParkedAsync(system.Hotels)).ShouldContain("hotels.reserve-room.v1", "the first request was lost to the outage");
        (await system.HotelEventsAsync(hotel)).ShouldBe([Registered, Held], "the retry held the room once");
        await system.GuestAsync(new ConfirmByGuest(booking));
        await system.WaitAsync(booking, s => s.Status == "Completed", "the booking to be confirmed");
        await system.WaitForRoomsAsync(hotel, held: 0, confirmed: 1);
    }
}

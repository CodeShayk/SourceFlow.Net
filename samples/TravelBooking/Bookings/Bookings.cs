using System.Text.Json.Serialization;
using Hotels;
using SourceFlow;
using SourceFlow.Hosting;

namespace Bookings;

/// <summary>The booking id (UUIDv7). It is also the trip-booking workflow's instance id.</summary>
[StronglyTypedId]
public readonly partial record struct BookingId(Guid Value);

/// <summary>Where a booking is in its life.</summary>
public enum BookingStatus
{
    /// <summary>Not requested yet.</summary>
    None = 0,

    /// <summary>Requested; the trip-booking workflow holds a room and waits for the guest.</summary>
    Requested = 1,

    /// <summary>The hotel confirmed the reservation.</summary>
    Confirmed = 2,

    /// <summary>The booking could not be made (the hotel was sold out, or the guest didn't confirm in time).</summary>
    Declined = 3,

    /// <summary>The guest cancelled before the reservation was confirmed.</summary>
    Cancelled = 4,
}

/// <summary>The rejection codes of the Bookings service.</summary>
public static class BookingErrors
{
    /// <summary>A stay is 1 to 30 nights.</summary>
    public const string InvalidStay = "bookings.invalid_stay";

    /// <summary>A booking needs the guest's name.</summary>
    public const string GuestRequired = "bookings.guest_required";

    /// <summary>The command needs a booking that is still being made.</summary>
    public const string NotOpen = "bookings.not_open";
}

/// <summary>A guest asks for a room: it starts the trip-booking workflow.</summary>
[MessageType("bookings.request-booking", 1)]
public sealed record RequestBooking(BookingId BookingId, Guid HotelId, string Guest, int Nights) : ICommand<Booking>;

/// <summary>The guest confirms the booking while the room is held.</summary>
[MessageType("bookings.confirm-by-guest", 1)]
public sealed record ConfirmByGuest(BookingId BookingId) : ICommand<Booking>;

/// <summary>The guest cancels the booking before it is confirmed.</summary>
[MessageType("bookings.cancel-by-guest", 1)]
public sealed record CancelByGuest(BookingId BookingId) : ICommand<Booking>;

/// <summary>Confirms the booking (sent by the workflow when the hotel confirmed the reservation).</summary>
[MessageType("bookings.confirm-booking", 1)]
public sealed record ConfirmBooking(BookingId BookingId) : ICommand<Booking>;

/// <summary>Declines the booking (sent by the workflow once the hold was released).</summary>
[MessageType("bookings.decline-booking", 1)]
public sealed record DeclineBooking(BookingId BookingId, string Reason) : ICommand<Booking>;

/// <summary>A booking was requested.</summary>
[MessageType("bookings.booking-requested", 1)]
public sealed record BookingRequested(BookingId BookingId, Guid HotelId, [property: PersonalData] string Guest, int Nights) : IEvent;

/// <summary>The guest confirmed the booking.</summary>
[MessageType("bookings.guest-confirmed", 1)]
public sealed record GuestConfirmed(BookingId BookingId) : IEvent;

/// <summary>The guest cancelled the booking.</summary>
[MessageType("bookings.guest-cancelled", 1)]
public sealed record GuestCancelled(BookingId BookingId) : IEvent;

/// <summary>The booking is confirmed: the hotel holds a reservation for the guest.</summary>
[MessageType("bookings.booking-confirmed", 1)]
public sealed record BookingConfirmed(BookingId BookingId) : IEvent;

/// <summary>The booking was declined.</summary>
[MessageType("bookings.booking-declined", 1)]
public sealed record BookingDeclined(BookingId BookingId, string Reason) : IEvent;

/// <summary>The booking aggregate: pure deciders (ADR-003). The trip-booking workflow talks to the hotel.</summary>
[Aggregate("booking")]
public sealed partial record Booking : IAggregate<Booking, BookingId>
{
    /// <summary>The state before the first event.</summary>
    public static Booking Initial { get; } = new();

    /// <summary>Where the booking is.</summary>
    public BookingStatus Status { get; init; }

    /// <summary>True once the guest confirmed.</summary>
    public bool ConfirmedByGuest { get; init; }

    /// <summary>Requests the booking once.</summary>
    public Decision Decide(RequestBooking c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Status != BookingStatus.None)
        {
            return Decision.NoOp;
        }

        if (string.IsNullOrWhiteSpace(c.Guest))
        {
            return Decision.Reject(new Error(BookingErrors.GuestRequired, "A booking needs the guest's name."));
        }

        return c.Nights is < 1 or > 30
            ? Decision.Reject(new Error(BookingErrors.InvalidStay, "A stay is 1 to 30 nights."))
            : Decision.Accept(new BookingRequested(c.BookingId, c.HotelId, c.Guest, c.Nights));
    }

    /// <summary>The guest confirms an open booking once.</summary>
    public Decision Decide(ConfirmByGuest c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Status != BookingStatus.Requested)
        {
            return Decision.Reject(new Error(BookingErrors.NotOpen, "Only a booking that is still being made can be confirmed."));
        }

        return ConfirmedByGuest ? Decision.NoOp : Decision.Accept(new GuestConfirmed(c.BookingId));
    }

    /// <summary>The guest cancels an open booking that the hotel hasn't confirmed yet.</summary>
    public Decision Decide(CancelByGuest c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == BookingStatus.Requested && !ConfirmedByGuest
            ? Decision.Accept(new GuestCancelled(c.BookingId))
            : Decision.Reject(new Error(BookingErrors.NotOpen, "Only a booking the guest hasn't confirmed can be cancelled."));
    }

    /// <summary>Confirms an open booking, once.</summary>
    public Decision Decide(ConfirmBooking c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == BookingStatus.Requested ? Decision.Accept(new BookingConfirmed(c.BookingId)) : Decision.NoOp;
    }

    /// <summary>Declines an open booking, once. A booking the guest cancelled stays cancelled.</summary>
    public Decision Decide(DeclineBooking c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == BookingStatus.Requested ? Decision.Accept(new BookingDeclined(c.BookingId, c.Reason)) : Decision.NoOp;
    }

    private Booking When(BookingRequested e)
    {
        return this with { Status = BookingStatus.Requested };
    }

    private Booking When(GuestConfirmed e)
    {
        return this with { ConfirmedByGuest = true };
    }

    private Booking When(GuestCancelled e)
    {
        return this with { Status = BookingStatus.Cancelled };
    }

    private Booking When(BookingConfirmed e)
    {
        return this with { Status = BookingStatus.Confirmed };
    }

    private Booking When(BookingDeclined e)
    {
        return this with { Status = BookingStatus.Declined };
    }
}

/// <summary>A booking as the guest sees it.</summary>
/// <param name="Id">The booking.</param>
/// <param name="HotelId">The hotel.</param>
/// <param name="Guest">The guest's name (personal data).</param>
/// <param name="Nights">The length of the stay.</param>
/// <param name="Status">Where the booking is.</param>
/// <param name="ConfirmedByGuest">True once the guest confirmed.</param>
/// <param name="Reason">Why the booking was declined, if it was.</param>
public sealed record BookingSummary(
    BookingId Id,
    Guid HotelId,
    [property: PersonalData] string Guest,
    int Nights,
    [property: Queryable] BookingStatus Status,
    bool ConfirmedByGuest,
    string? Reason);

/// <summary>Maintains <see cref="BookingSummary"/> documents.</summary>
[Projection("booking-summary")]
public sealed partial class BookingSummaryProjection : IDocumentProjection<BookingSummary, BookingId>
{
    private static BookingSummary When(BookingRequested e, BookingSummary? current, EventContext ctx)
    {
        return new BookingSummary(e.BookingId, e.HotelId, e.Guest, e.Nights, BookingStatus.Requested, false, null);
    }

    private static BookingSummary? When(GuestConfirmed e, BookingSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { ConfirmedByGuest = true };
    }

    private static BookingSummary? When(GuestCancelled e, BookingSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Status = BookingStatus.Cancelled };
    }

    private static BookingSummary? When(BookingConfirmed e, BookingSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Status = BookingStatus.Confirmed };
    }

    private static BookingSummary? When(BookingDeclined e, BookingSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Status = BookingStatus.Declined, Reason = e.Reason };
    }
}

/// <summary>The bookings in a status: a named, versioned domain query (doc 14 Q-B4) the demo serves over HTTP.</summary>
/// <param name="Status">The status, as its number (1 requested, 2 confirmed, 3 declined, 4 cancelled).</param>
[DomainQuery("bookings.by-status", 1)]
public sealed partial record BookingsByStatus(int Status) : IDomainQuery<BookingRow>
{
    /// <summary>The query text.</summary>
    public const string Sfql = """
        from readmodel "booking-summary"
        | where status = @status
        | project bookingId = id, hotelId, nights, confirmedByGuest, reason
        | order by bookingId
        """;
}

/// <summary>A row of <see cref="BookingsByStatus"/>.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="HotelId">The hotel.</param>
/// <param name="Nights">The length of the stay.</param>
/// <param name="ConfirmedByGuest">True once the guest confirmed.</param>
/// <param name="Reason">Why the booking was declined, if it was.</param>
public sealed record BookingRow(BookingId BookingId, Guid HotelId, int Nights, bool ConfirmedByGuest, string? Reason);

/// <summary>The Bookings domain: the booking aggregate, its read model and, generated from Workflows/*.sfflow.yaml, the trip-booking workflow.</summary>
[SourceFlowModule(typeof(BookingsJsonContext))]
public sealed partial class BookingsDomainModule;

/// <summary>
/// The Bookings service's module: its domain and the contract of the Hotels service, whose commands the workflow sends
/// and whose events it awaits. It is the module the workspace names for the workflow's specs
/// (<c>sourceflow.domain.yaml</c>: <c>module</c>), so a spec runs the workflow exactly as the service does.
/// </summary>
public sealed class BookingsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(BookingsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        BookingsDomainModule.Register(builder);
        HotelsContractsModule.Register(builder);
    }
}

/// <summary>How the Bookings service is set up: what its host, the design-time export and the tests share.</summary>
public static class BookingsService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "bookings";

    /// <summary>
    /// Adds the Bookings service: its module (with the Hotels contract), the booking read model (inline), the routes of the
    /// Hotels commands (with a rejection reply for the ones the workflow handles), and the subscription to Hotels' events.
    /// </summary>
    public static SourceFlowBuilder AddBookingsService(this SourceFlowBuilder sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        return sf
            .WithServiceName(Name)
            .AddModule<BookingsModule>()
            .Projections(p => p.Add<BookingSummaryProjection>().Inline())
            .Routing(r =>
            {
                r.Command<ReserveRoom>().To(HotelsService.Name).ReplyOnReject();
                r.Command<ReleaseRoom>().To(HotelsService.Name);
                r.Command<ConfirmReservation>().To(HotelsService.Name).ReplyOnReject();
                r.Subscribe(HotelTopics.Events);
            });
    }
}

/// <summary>JSON metadata for the Bookings service (DC-1), with the rejection replies the workflow handles.</summary>
[JsonSerializable(typeof(RequestBooking))]
[JsonSerializable(typeof(ConfirmByGuest))]
[JsonSerializable(typeof(CancelByGuest))]
[JsonSerializable(typeof(ConfirmBooking))]
[JsonSerializable(typeof(DeclineBooking))]
[JsonSerializable(typeof(BookingRequested))]
[JsonSerializable(typeof(GuestConfirmed))]
[JsonSerializable(typeof(GuestCancelled))]
[JsonSerializable(typeof(BookingConfirmed))]
[JsonSerializable(typeof(BookingDeclined))]
[JsonSerializable(typeof(Booking))]
[JsonSerializable(typeof(BookingSummary))]
[JsonSerializable(typeof(BookingRow))]
[JsonSerializable(typeof(Hotels.BookingId), TypeInfoPropertyName = "HotelsBookingId")]
[JsonSerializable(typeof(CommandRejected<ReserveRoom>))]
[JsonSerializable(typeof(CommandRejected<ConfirmReservation>))]
[JsonSerializable(typeof(CommandRejected<ReleaseRoom>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BookingsJsonContext : JsonSerializerContext;

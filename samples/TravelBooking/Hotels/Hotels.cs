using System.Text.Json.Serialization;
using SourceFlow;
using SourceFlow.Hosting;

namespace Hotels;

/// <summary>A hotel (UUIDv7).</summary>
[StronglyTypedId]
public readonly partial record struct HotelId(Guid Value);

/// <summary>A booking, as the Hotels service knows it: the id of the hold it places for the Bookings service.</summary>
[StronglyTypedId]
public readonly partial record struct BookingId(Guid Value);

/// <summary>The rejection codes of the Hotels service.</summary>
public static class HotelErrors
{
    /// <summary>The hotel was never registered.</summary>
    public const string UnknownHotel = "hotels.unknown_hotel";

    /// <summary>Every room is held or booked.</summary>
    public const string SoldOut = "hotels.sold_out";

    /// <summary>The hold to confirm no longer exists: it was released.</summary>
    public const string HoldLost = "hotels.hold_lost";

    /// <summary>A hotel needs at least one room.</summary>
    public const string InvalidRooms = "hotels.invalid_rooms";
}

// ================================================================ contract: what the Bookings service sends and awaits

/// <summary>Holds a room for a booking until the booking is confirmed or the hold is released.</summary>
[MessageType("hotels.reserve-room", 1)]
public sealed record ReserveRoom(HotelId HotelId, BookingId BookingId, int Nights) : ICommand<Hotel>;

/// <summary>Releases the room held for a booking (the workflow's compensation, or hotel staff).</summary>
[MessageType("hotels.release-room", 1)]
public sealed record ReleaseRoom(HotelId HotelId, BookingId BookingId) : ICommand<Hotel>;

/// <summary>Turns a booking's hold into a confirmed reservation.</summary>
[MessageType("hotels.confirm-reservation", 1)]
public sealed record ConfirmReservation(HotelId HotelId, BookingId BookingId) : ICommand<Hotel>;

/// <summary>A room is held for a booking.</summary>
[MessageType("hotels.room-reserved", 1)]
public sealed record RoomReserved(HotelId HotelId, BookingId BookingId, int Nights) : IEvent;

/// <summary>The room held for a booking was released, or there was none to release.</summary>
[MessageType("hotels.room-released", 1)]
public sealed record RoomReleased(HotelId HotelId, BookingId BookingId) : IEvent;

/// <summary>A booking's hold became a confirmed reservation.</summary>
[MessageType("hotels.reservation-confirmed", 1)]
public sealed record ReservationConfirmed(HotelId HotelId, BookingId BookingId) : IEvent;

// ================================================================ the Hotels service's own messages

/// <summary>Registers a hotel and its number of rooms.</summary>
[MessageType("hotels.register-hotel", 1)]
public sealed record RegisterHotel(HotelId HotelId, string Name, int Rooms) : ICommand<Hotel>;

/// <summary>A hotel was registered.</summary>
[MessageType("hotels.hotel-registered", 1)]
public sealed record HotelRegistered(HotelId HotelId, string Name, int Rooms) : IEvent;

/// <summary>
/// A hotel's rooms: how many it has, which bookings hold one and which are confirmed. A room is free when it is neither
/// held nor confirmed, so holds and confirmations together never exceed the rooms.
/// </summary>
[Aggregate("hotel")]
public sealed partial record Hotel : IAggregate<Hotel, HotelId>
{
    /// <summary>The state before the first event.</summary>
    public static Hotel Initial { get; } = new();

    /// <summary>The hotel's name; null until it is registered.</summary>
    public string? Name { get; init; }

    /// <summary>The number of rooms; 0 until the hotel is registered.</summary>
    public int Rooms { get; init; }

    /// <summary>The bookings holding a room.</summary>
    public IReadOnlyList<BookingId> Held { get; init; } = [];

    /// <summary>The bookings with a confirmed reservation.</summary>
    public IReadOnlyList<BookingId> Confirmed { get; init; } = [];

    /// <summary>Registers the hotel once.</summary>
    public Decision Decide(RegisterHotel c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Rooms > 0)
        {
            return Decision.NoOp;
        }

        return c.Rooms <= 0
            ? Decision.Reject(new Error(HotelErrors.InvalidRooms, "A hotel needs at least one room."))
            : Decision.Accept(new HotelRegistered(c.HotelId, c.Name, c.Rooms));
    }

    /// <summary>Holds a room when one is free. A booking that already holds or booked a room is a no-op.</summary>
    public Decision Decide(ReserveRoom c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Rooms == 0)
        {
            return Decision.Reject(new Error(HotelErrors.UnknownHotel, "The hotel is not registered."));
        }

        if (Held.Contains(c.BookingId) || Confirmed.Contains(c.BookingId))
        {
            return Decision.NoOp;
        }

        return Held.Count + Confirmed.Count >= Rooms
            ? Decision.Reject(new Error(HotelErrors.SoldOut, $"Every room of {Name} is taken."))
            : Decision.Accept(new RoomReserved(c.HotelId, c.BookingId, c.Nights));
    }

    /// <summary>Releases a booking's hold. Releasing what isn't held still answers: the compensation must end.</summary>
    public Decision Decide(ReleaseRoom c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Decision.Accept(new RoomReleased(c.HotelId, c.BookingId));
    }

    /// <summary>Confirms a held room; a hold that was released can't be confirmed.</summary>
    public Decision Decide(ConfirmReservation c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Confirmed.Contains(c.BookingId))
        {
            return Decision.NoOp;
        }

        return Held.Contains(c.BookingId)
            ? Decision.Accept(new ReservationConfirmed(c.HotelId, c.BookingId))
            : Decision.Reject(new Error(HotelErrors.HoldLost, "The booking holds no room: the hold was released."));
    }

    private Hotel When(HotelRegistered e)
    {
        return this with { Name = e.Name, Rooms = e.Rooms };
    }

    private Hotel When(RoomReserved e)
    {
        return this with { Held = [.. Held, e.BookingId] };
    }

    private Hotel When(RoomReleased e)
    {
        return this with { Held = [.. Held.Where(b => b != e.BookingId)] };
    }

    private Hotel When(ReservationConfirmed e)
    {
        return this with { Held = [.. Held.Where(b => b != e.BookingId)], Confirmed = [.. Confirmed, e.BookingId] };
    }
}

/// <summary>A hotel's rooms as a read model: what the hotel staff and the query API see.</summary>
/// <param name="Id">The hotel.</param>
/// <param name="Name">Its name.</param>
/// <param name="Rooms">Its rooms.</param>
/// <param name="Held">Rooms held for bookings.</param>
/// <param name="Confirmed">Rooms with a confirmed reservation.</param>
public sealed record HotelRooms(HotelId Id, string Name, int Rooms, [property: Queryable] int Held, int Confirmed)
{
    /// <summary>Rooms neither held nor confirmed.</summary>
    public int Free => Rooms - Held - Confirmed;
}

/// <summary>Maintains <see cref="HotelRooms"/> documents.</summary>
[Projection("hotel-rooms")]
public sealed partial class HotelRoomsProjection : IDocumentProjection<HotelRooms, HotelId>
{
    private static HotelRooms When(HotelRegistered e, HotelRooms? current, EventContext ctx)
    {
        return new HotelRooms(e.HotelId, e.Name, e.Rooms, 0, 0);
    }

    private static HotelRooms? When(RoomReserved e, HotelRooms? current, EventContext ctx)
    {
        return current is null ? null : current with { Held = current.Held + 1 };
    }

    private static HotelRooms? When(RoomReleased e, HotelRooms? current, EventContext ctx)
    {
        // A release of a hold that didn't exist still answers (see Hotel.Decide(ReleaseRoom)): never go below zero.
        return current is null ? null : current with { Held = Math.Max(0, current.Held - 1) };
    }

    private static HotelRooms? When(ReservationConfirmed e, HotelRooms? current, EventContext ctx)
    {
        return current is null ? null : current with { Held = Math.Max(0, current.Held - 1), Confirmed = current.Confirmed + 1 };
    }
}

/// <summary>The Hotels service's topic.</summary>
public static class HotelTopics
{
    /// <summary>Where Hotels publishes what the Bookings service awaits.</summary>
    public const string Events = "hotels.events";
}

/// <summary>The Hotels module: the hotel aggregate and its read model.</summary>
[SourceFlowModule(typeof(HotelsJsonContext))]
public sealed partial class HotelsModule;

/// <summary>
/// The Hotels contract for other services: the commands it accepts from them and the events it publishes, without the
/// aggregate that decides them. The Bookings service registers this module and routes the commands to <c>hotels</c>.
/// </summary>
public sealed class HotelsContractsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(HotelsContractsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<ReserveRoom>("hotels.reserve-room", 1);
        builder.AddMessage<ReleaseRoom>("hotels.release-room", 1);
        builder.AddMessage<ConfirmReservation>("hotels.confirm-reservation", 1);
        builder.AddMessage<RoomReserved>("hotels.room-reserved", 1);
        builder.AddMessage<RoomReleased>("hotels.room-released", 1);
        builder.AddMessage<ReservationConfirmed>("hotels.reservation-confirmed", 1);
        builder.AddJsonTypeInfoResolver(HotelsJsonContext.Default);
        builder.AddJsonConverter(new HotelId.SourceFlowJsonConverter());
        builder.AddJsonConverter(new BookingId.SourceFlowJsonConverter());
    }
}

/// <summary>JSON metadata for the Hotels service (DC-1).</summary>
[JsonSerializable(typeof(RegisterHotel))]
[JsonSerializable(typeof(ReserveRoom))]
[JsonSerializable(typeof(ReleaseRoom))]
[JsonSerializable(typeof(ConfirmReservation))]
[JsonSerializable(typeof(HotelRegistered))]
[JsonSerializable(typeof(RoomReserved))]
[JsonSerializable(typeof(RoomReleased))]
[JsonSerializable(typeof(ReservationConfirmed))]
[JsonSerializable(typeof(Hotel))]
[JsonSerializable(typeof(HotelRooms))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class HotelsJsonContext : JsonSerializerContext;

/// <summary>How the Hotels service is set up: what its host, the design-time export and the tests share.</summary>
public static class HotelsService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "hotels";

    /// <summary>
    /// Adds the Hotels service: its module, the rooms read model (inline, so a hotel's rooms are current as soon as a
    /// command returns), and the publication of what the Bookings service awaits to <see cref="HotelTopics.Events"/>.
    /// </summary>
    public static SourceFlowBuilder AddHotelsService(this SourceFlowBuilder sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        return sf
            .WithServiceName(Name)
            .AddModule<HotelsModule>()
            .Projections(p => p.Add<HotelRoomsProjection>().Inline())
            .Routing(r =>
            {
                r.Publish<RoomReserved>().AsIs().To(HotelTopics.Events);
                r.Publish<RoomReleased>().AsIs().To(HotelTopics.Events);
                r.Publish<ReservationConfirmed>().AsIs().To(HotelTopics.Events);
            });
    }
}

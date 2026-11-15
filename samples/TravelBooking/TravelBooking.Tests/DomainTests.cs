using Bookings;
using Hotels;
using SourceFlow.Testing;
using HotelsBookingId = Hotels.BookingId;

namespace TravelBooking.Tests;

/// <summary>The two aggregates, decision by decision (Given/When/Then).</summary>
public sealed class HotelTests
{
    private static readonly HotelId Hotel = HotelId.Parse("0193a1b2-c3d4-7e5f-8a9b-000000000001");
    private static readonly HotelsBookingId First = HotelsBookingId.Parse("0193a1b2-c3d4-7e5f-8a9b-0000000000a1");
    private static readonly HotelsBookingId Second = HotelsBookingId.Parse("0193a1b2-c3d4-7e5f-8a9b-0000000000a2");

    [Fact]
    public void Register_ThenHold_UntilEveryRoomIsTaken()
    {
        new AggregateSpec<Hotel, HotelId>()
            .When(new RegisterHotel(Hotel, "Seaview", 1))
            .Then(new HotelRegistered(Hotel, "Seaview", 1));

        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1))
            .When(new ReserveRoom(Hotel, First, 2))
            .Then(new RoomReserved(Hotel, First, 2))
            .ThenState(s => s.Held.Count == 1);

        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1), new RoomReserved(Hotel, First, 2))
            .When(new ReserveRoom(Hotel, Second, 1))
            .ThenRejected(HotelErrors.SoldOut);
    }

    [Fact]
    public void A_Hotel_Needs_Rooms_And_Registration()
    {
        new AggregateSpec<Hotel, HotelId>()
            .When(new RegisterHotel(Hotel, "Nowhere", 0))
            .ThenRejected(HotelErrors.InvalidRooms);

        new AggregateSpec<Hotel, HotelId>()
            .When(new ReserveRoom(Hotel, First, 1))
            .ThenRejected(HotelErrors.UnknownHotel);
    }

    [Fact]
    public void Holding_Twice_IsANoOp_AndReleasingFreesTheRoom()
    {
        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1), new RoomReserved(Hotel, First, 2))
            .When(new ReserveRoom(Hotel, First, 2))
            .ThenNoOp();

        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1), new RoomReserved(Hotel, First, 2), new RoomReleased(Hotel, First))
            .When(new ReserveRoom(Hotel, Second, 1))
            .Then(new RoomReserved(Hotel, Second, 1));
    }

    [Fact]
    public void Releasing_AlwaysAnswers_SoTheCompensationEnds()
    {
        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1))
            .When(new ReleaseRoom(Hotel, First))
            .Then(new RoomReleased(Hotel, First));
    }

    [Fact]
    public void Confirming_NeedsTheHold()
    {
        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1), new RoomReserved(Hotel, First, 2))
            .When(new ConfirmReservation(Hotel, First))
            .Then(new ReservationConfirmed(Hotel, First))
            .ThenState(s => s.Held.Count == 0 && s.Confirmed.Count == 1);

        new AggregateSpec<Hotel, HotelId>()
            .Given(new HotelRegistered(Hotel, "Seaview", 1), new RoomReserved(Hotel, First, 2), new RoomReleased(Hotel, First))
            .When(new ConfirmReservation(Hotel, First))
            .ThenRejected(HotelErrors.HoldLost);
    }
}

/// <summary>The booking aggregate.</summary>
public sealed class BookingTests
{
    private static readonly Bookings.BookingId Booking = Bookings.BookingId.Parse("0193a1b2-c3d4-7e5f-8a9b-0000000000b1");
    private static readonly Guid Hotel = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-000000000001");

    [Fact]
    public void Request_ValidatesTheStay_AndTheGuest()
    {
        new AggregateSpec<Booking, Bookings.BookingId>()
            .When(new RequestBooking(Booking, Hotel, "Ada", 2))
            .Then(new BookingRequested(Booking, Hotel, "Ada", 2))
            .ThenState(s => s.Status == BookingStatus.Requested);

        new AggregateSpec<Booking, Bookings.BookingId>()
            .When(new RequestBooking(Booking, Hotel, "Ada", 0))
            .ThenRejected(BookingErrors.InvalidStay);

        new AggregateSpec<Booking, Bookings.BookingId>()
            .When(new RequestBooking(Booking, Hotel, " ", 2))
            .ThenRejected(BookingErrors.GuestRequired);
    }

    [Fact]
    public void TheGuest_ConfirmsOnce_OrCancelsBeforeConfirming()
    {
        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2))
            .When(new ConfirmByGuest(Booking))
            .Then(new GuestConfirmed(Booking));

        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2), new GuestConfirmed(Booking))
            .When(new CancelByGuest(Booking))
            .ThenRejected(BookingErrors.NotOpen);

        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2))
            .When(new CancelByGuest(Booking))
            .Then(new GuestCancelled(Booking))
            .ThenState(s => s.Status == BookingStatus.Cancelled);
    }

    [Fact]
    public void TheWorkflow_ConfirmsOrDeclines_Once()
    {
        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2))
            .When(new ConfirmBooking(Booking))
            .Then(new BookingConfirmed(Booking));

        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2), new BookingConfirmed(Booking))
            .When(new DeclineBooking(Booking, "late"))
            .ThenNoOp();

        // A cancelled booking stays cancelled when the workflow then declines it.
        new AggregateSpec<Booking, Bookings.BookingId>()
            .Given(new BookingRequested(Booking, Hotel, "Ada", 2), new GuestCancelled(Booking))
            .When(new DeclineBooking(Booking, "bookings.guest-cancelled.v1"))
            .ThenNoOp();
    }
}

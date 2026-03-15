using System;
using System.Text.Json;
using SourceFlow.Cloud.Security;

namespace SourceFlow.Core.Tests.Cloud
{
    [TestFixture]
    [Category("Unit")]
    public class SensitiveDataMaskerTests
    {
        private SensitiveDataMasker _masker = null!;

        // ── Test helper types ─────────────────────────────────────────────────────

        private class PaymentInfo
        {
            [SensitiveData(SensitiveDataType.CreditCard)]
            public string CardNumber { get; set; } = "";

            [SensitiveData(SensitiveDataType.Email)]
            public string Email { get; set; } = "";
        }

        private class PersonInfo
        {
            [SensitiveData(SensitiveDataType.PhoneNumber)]
            public string Phone { get; set; } = "";

            [SensitiveData(SensitiveDataType.SSN)]
            public string Ssn { get; set; } = "";

            [SensitiveData(SensitiveDataType.PersonalName)]
            public string FullName { get; set; } = "";

            [SensitiveData(SensitiveDataType.IPAddress)]
            public string IpAddress { get; set; } = "";

            [SensitiveData(SensitiveDataType.Password)]
            public string Password { get; set; } = "";

            [SensitiveData(SensitiveDataType.ApiKey)]
            public string ApiKey { get; set; } = "";
        }

        private class PlainObject
        {
            public string Name { get; set; } = "";
            public int Value { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            _masker = new SensitiveDataMasker();
        }

        // ── CreditCard ────────────────────────────────────────────────────────────

        [Test]
        public void Mask_CreditCard_ShowsLastFourDigits()
        {
            var obj = new PaymentInfo { CardNumber = "4111111111111234", Email = "x@example.com" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("1234"));
            // First digits should be masked
            Assert.That(result, Does.Contain("*"));
        }

        // ── Email ─────────────────────────────────────────────────────────────────

        [Test]
        public void Mask_Email_ShowsDomainOnlyWithTripleStarPrefix()
        {
            var obj = new PaymentInfo { CardNumber = "1234", Email = "user@example.com" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("***@example.com"));
            Assert.That(result, Does.Not.Contain("user@"));
        }

        // ── PhoneNumber ───────────────────────────────────────────────────────────

        [Test]
        public void Mask_PhoneNumber_ShowsLastFourDigits()
        {
            var obj = new PersonInfo { Phone = "5551234567" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("4567"));
            Assert.That(result, Does.Contain("***-***-"));
        }

        // ── SSN ───────────────────────────────────────────────────────────────────

        [Test]
        public void Mask_Ssn_ShowsLastFourDigits()
        {
            var obj = new PersonInfo { Ssn = "123-45-6789" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("6789"));
            Assert.That(result, Does.Contain("***-**-"));
        }

        // ── PersonalName ──────────────────────────────────────────────────────────

        [Test]
        public void Mask_PersonalName_ShowsFirstLetterOfEachWord()
        {
            var obj = new PersonInfo { FullName = "John Doe" };

            var result = _masker.Mask(obj);

            // First letter of each word should be visible
            Assert.That(result, Does.Contain("J"));
            Assert.That(result, Does.Contain("D"));
            // Rest should be masked
            Assert.That(result, Does.Contain("*"));
        }

        // ── IPAddress ─────────────────────────────────────────────────────────────

        [Test]
        public void Mask_IpAddress_ShowsFirstOctetOnly()
        {
            var obj = new PersonInfo { IpAddress = "192.168.1.100" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("192.*.*.*"));
        }

        // ── Password ──────────────────────────────────────────────────────────────

        [Test]
        public void Mask_Password_FullyRedacted()
        {
            var obj = new PersonInfo { Password = "supersecretpassword" };

            var result = _masker.Mask(obj);

            Assert.That(result, Does.Contain("********"));
            Assert.That(result, Does.Not.Contain("supersecret"));
        }

        // ── ApiKey ────────────────────────────────────────────────────────────────

        [Test]
        public void Mask_ApiKey_ShowsFirstAndLastFourChars()
        {
            var obj = new PersonInfo { ApiKey = "abcd1234efgh5678" };

            var result = _masker.Mask(obj);

            // First 4 and last 4 should be visible with "..." in between
            Assert.That(result, Does.Contain("abcd"));
            Assert.That(result, Does.Contain("5678"));
            Assert.That(result, Does.Contain("..."));
        }

        // ── Null input ────────────────────────────────────────────────────────────

        [Test]
        public void Mask_NullInput_ReturnsNullStringWithoutThrowing()
        {
            var result = _masker.Mask(null);

            Assert.That(result, Is.EqualTo("null"));
        }

        // ── Object with no sensitive attributes ───────────────────────────────────

        [Test]
        public void Mask_ObjectWithNoSensitiveAttributes_ReturnedUnchanged()
        {
            var obj = new PlainObject { Name = "Alice", Value = 42 };

            var result = _masker.Mask(obj);

            // Should contain the original values since nothing is marked sensitive
            Assert.That(result, Does.Contain("Alice"));
            Assert.That(result, Does.Contain("42"));
        }

        // ── MaskLazy ─────────────────────────────────────────────────────────────

        [Test]
        public void MaskLazy_ToStringDelegatestoMask()
        {
            var obj = new PaymentInfo { CardNumber = "4111111111111234", Email = "user@example.com" };

            var lazy = _masker.MaskLazy(obj);

            var result = lazy.ToString();
            Assert.That(result, Does.Contain("***@example.com"));
        }
    }
}

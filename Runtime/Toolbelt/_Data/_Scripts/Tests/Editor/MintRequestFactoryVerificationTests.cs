using System.Linq;
using NUnit.Framework;
using Solana.Unity.SDK;

namespace Solana.Unity.Toolbelt.Tests.Editor
{
    public class MintRequestFactoryVerificationTests
    {
        [SetUp]
        public void SetUp()
        {
            ResetSessionWalletStatics();
        }

        [TearDown]
        public void TearDown()
        {
            ResetSessionWalletStatics();
        }

        [Test]
        public void NonSignerCreatorsAreMarkedUnverified()
        {
            var factory = new DefaultMintRequestFactory();
            var manifestHash = Enumerable.Repeat((byte)0x42, 32).ToArray();

            var creators = new[]
            {
                new OwnerGovernedAssetLedgerCreator("11111111111111111111111111111111", true, 100)
            };

            var request = factory.CreateMintRequest(
                1UL,
                "https://example.com/manifest.json",
                "22222222222222222222222222222222",
                manifestHash,
                "Test Level",
                "TL",
                500,
                creators);

            Assert.AreEqual(1, request.Creators.Count);
            Assert.IsFalse(request.Creators[0].Verified);
        }

        private static void ResetSessionWalletStatics()
        {
            var instanceField = typeof(SessionWallet).GetField("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            instanceField?.SetValue(null, null);

            var externalField = typeof(SessionWallet).GetField("_externalWallet", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            externalField?.SetValue(null, null);
        }
    }
}

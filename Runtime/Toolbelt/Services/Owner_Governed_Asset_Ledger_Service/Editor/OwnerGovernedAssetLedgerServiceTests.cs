#if UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Solana.Unity.Programs;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Toolbelt;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt.Tests
{
    public class OwnerGovernedAssetLedgerServiceTests
    {
        private const int PublicKeyLength = 32;

        private static IReadOnlyList<OwnerGovernedAssetLedgerCreator> InvokeSanitizeMintCreators(
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            PublicKey payer,
            IReadOnlyList<PublicKey> permittedSigners = null)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "SanitizeMintCreators",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate SanitizeMintCreators via reflection.");

            permittedSigners ??= new List<PublicKey> { payer };

            try
            {
                return (IReadOnlyList<OwnerGovernedAssetLedgerCreator>)method.Invoke(
                    null,
                    new object[]
                    {
                        creators,
                        payer,
                        permittedSigners
                    });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        private static IReadOnlyList<string> InvokeFindMissingVerifiedCreatorSignatures(
            Transaction transaction,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "FindMissingVerifiedCreatorSignatures",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate FindMissingVerifiedCreatorSignatures via reflection.");

            try
            {
                return (IReadOnlyList<string>)method.Invoke(
                    null,
                    new object[]
                    {
                        transaction,
                        creators
                    });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        private static IReadOnlyList<OwnerGovernedAssetLedgerCreator> InvokeDowngradeCreatorsForMissingSignatures(
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            IReadOnlyCollection<string> missingSigners)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "DowngradeCreatorsForMissingSignatures",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate DowngradeCreatorsForMissingSignatures via reflection.");

            try
            {
                return (IReadOnlyList<OwnerGovernedAssetLedgerCreator>)method.Invoke(
                    null,
                    new object[]
                    {
                        creators,
                        missingSigners
                    });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        private static IReadOnlyList<TransactionInstruction> InvokeBuildMintTransactionInstructions(
            TransactionInstruction mintInstruction,
            uint computeUnitLimit,
            ulong? computeUnitPriceMicroLamports)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "BuildMintTransactionInstructions",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate BuildMintTransactionInstructions via reflection.");

            try
            {
                return (IReadOnlyList<TransactionInstruction>)method.Invoke(
                    null,
                    new object[]
                    {
                        mintInstruction,
                        computeUnitLimit,
                        computeUnitPriceMicroLamports
                    });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        private static IEnumerable<TestCaseData> MasterEditionV1ParsingTestCases()
        {
            const ulong supply = 7;

            yield return new TestCaseData(
                    BuildMasterEditionV1Bytes(supply, 0u, null),
                    supply,
                    (byte)0,
                    null,
                    false,
                    false)
                .SetName("TryParseMasterEditionLayout_V1_MaxSupplyNone");

            yield return new TestCaseData(
                    BuildMasterEditionV1Bytes(supply, 1u, 0ul),
                    supply,
                    (byte)1,
                    0ul,
                    true,
                    true)
                .SetName("TryParseMasterEditionLayout_V1_MaxSupplyZeroIsUnique");

            yield return new TestCaseData(
                    BuildMasterEditionV1Bytes(supply, 1u, 15ul),
                    supply,
                    (byte)1,
                    15ul,
                    true,
                    false)
                .SetName("TryParseMasterEditionLayout_V1_MaxSupplyNonZero");
        }

        private static IEnumerable<TestCaseData> MasterEditionV2ParsingTestCases()
        {
            const ulong supply = 11;

            yield return new TestCaseData(
                    BuildMasterEditionV2Bytes(supply, 0, null),
                    supply,
                    (byte)0,
                    null,
                    false,
                    false)
                .SetName("TryParseMasterEditionLayout_V2_MaxSupplyNone");

            yield return new TestCaseData(
                    BuildMasterEditionV2Bytes(supply, 1, 0ul),
                    supply,
                    (byte)1,
                    0ul,
                    true,
                    true)
                .SetName("TryParseMasterEditionLayout_V2_MaxSupplyZeroIsUnique");

            yield return new TestCaseData(
                    BuildMasterEditionV2Bytes(supply, 1, 42ul),
                    supply,
                    (byte)1,
                    42ul,
                    true,
                    false)
                .SetName("TryParseMasterEditionLayout_V2_MaxSupplyNonZero");
        }

        private static bool InvokeIsTransportError(Exception exception)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "IsTransportError",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate IsTransportError via reflection.");

            return (bool)method.Invoke(null, new object[] { exception });
        }

        private static bool InvokeIsTransportErrorMessage(string message)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "IsTransportErrorMessage",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate IsTransportErrorMessage via reflection.");

            return (bool)method.Invoke(null, new object[] { message });
        }

        private static int InvokeCalculateExponentialBackoffDelay(int attempt, int baseDelay)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "CalculateExponentialBackoffDelay",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate CalculateExponentialBackoffDelay via reflection.");

            return (int)method.Invoke(null, new object[] { attempt, baseDelay });
        }

        private static string InvokeDetermineTransportRetryDecision(
            int attempt,
            int maxAttempts,
            bool hasSecondary,
            bool usingSecondary)
        {
            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "DetermineTransportRetryDecision",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate DetermineTransportRetryDecision via reflection.");

            var result = method.Invoke(null, new object[]
            {
                attempt,
                maxAttempts,
                hasSecondary,
                usingSecondary
            });

            Assert.IsNotNull(result, "DetermineTransportRetryDecision returned null.");
            return result.ToString();
        }

        private static void AssertInstructionsEqual(TransactionInstruction expected, TransactionInstruction actual)
        {
            Assert.AreEqual(expected.ProgramId, actual.ProgramId);
            CollectionAssert.AreEqual(expected.Data ?? Array.Empty<byte>(), actual.Data ?? Array.Empty<byte>());

            var expectedKeys = expected.Keys ?? new List<AccountMeta>();
            var actualKeys = actual.Keys ?? new List<AccountMeta>();
            Assert.AreEqual(expectedKeys.Count, actualKeys.Count, "Instruction key counts should match.");

            for (int i = 0; i < expectedKeys.Count; i++)
            {
                Assert.AreEqual(expectedKeys[i].PublicKey, actualKeys[i].PublicKey, $"Public key mismatch at index {i}.");
                Assert.AreEqual(expectedKeys[i].IsSigner, actualKeys[i].IsSigner, $"IsSigner mismatch at index {i}.");
                Assert.AreEqual(expectedKeys[i].IsWritable, actualKeys[i].IsWritable, $"IsWritable mismatch at index {i}.");
            }
        }

        private static void AssertAccountMeta(
            AccountMeta accountMeta,
            PublicKey expectedKey,
            bool expectedSigner,
            bool expectedWritable,
            int index)
        {
            Assert.IsNotNull(accountMeta, $"Account meta at index {index} should not be null.");
            Assert.AreEqual(expectedKey, accountMeta.PublicKey, $"Public key mismatch at index {index}.");
            Assert.AreEqual(expectedSigner, accountMeta.IsSigner, $"IsSigner mismatch at index {index}.");
            Assert.AreEqual(expectedWritable, accountMeta.IsWritable, $"IsWritable mismatch at index {index}.");
        }

        [Test]
        public void CollectionSizedState_DetectedAndSkipsMasterEditionUniqueness()
        {
            var metadata = BuildCollectionMetadataBytes(includeCollectionDetails: true);
            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Sized metadata should return a sizing state.");
            Assert.IsTrue(sizedState.Value, "Sized metadata should report true.");
            Assert.IsFalse(
                OwnerGovernedAssetLedgerService.ShouldEnforceUniqueMasterEditionForTests(sizedState),
                "Sized collections should bypass the unique master edition guard.");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void CollectionSizedState_DetectedForStandardTokenVariants(byte tokenStandardVariant)
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: tokenStandardVariant);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Sized metadata should return a sizing state.");
            Assert.IsTrue(sizedState.Value, "Sized metadata should report true.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CollectionSizedState_DetectedForProgrammableTokenStandard(
            bool includeProgrammableConfig,
            bool includeProgrammableRuleSet)
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 4,
                programmableConfigOption: includeProgrammableConfig ? (byte)1 : (byte)0,
                programmableRuleSetOption: includeProgrammableRuleSet ? (byte)1 : (byte)0);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Sized metadata should return a sizing state.");
            Assert.IsTrue(sizedState.Value, "Sized metadata should report true.");
        }

        [Test]
        public void CollectionSizedState_DetectedForProgrammableEditionTokenStandard()
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 5,
                programmableConfigOption: 0);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Sized metadata should return a sizing state.");
            Assert.IsTrue(sizedState.Value, "Sized metadata should report true.");
        }

        [Test]
        public void CollectionSizedState_InvalidProgrammableConfigOption_ReturnsNull()
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 4,
                programmableConfigOption: 2);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsFalse(sizedState.HasValue, "Invalid programmable config option should be rejected.");
        }

        [Test]
        public void CollectionSizedState_InvalidProgrammableConfigVariant_ReturnsNull()
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 4,
                programmableConfigOption: 1,
                programmableConfigVariant: 1);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsFalse(sizedState.HasValue, "Invalid programmable config variant should be rejected.");
        }

        [Test]
        public void CollectionSizedState_InvalidProgrammableRuleSetOption_ReturnsNull()
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 4,
                programmableConfigOption: 1,
                programmableRuleSetOption: 2);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsFalse(sizedState.HasValue, "Invalid programmable rule set option should be rejected.");
        }

        [Test]
        public void CollectionSizedState_DetectedForDirectTokenStandardEncoding()
        {
            var metadata = BuildCollectionMetadataBytes(
                includeCollectionDetails: true,
                tokenStandardVariant: 2,
                encodeTokenStandardAsOption: false);

            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Sized metadata should return a sizing state.");
            Assert.IsTrue(sizedState.Value, "Sized metadata should report true.");
        }

        [Test]
        public void CollectionUnsizedState_DetectedAndRequiresMasterEditionUniqueness()
        {
            var metadata = BuildCollectionMetadataBytes(includeCollectionDetails: false);
            bool? sizedState = OwnerGovernedAssetLedgerService.TryDetectCollectionSizedStateForTests(metadata);

            Assert.IsTrue(sizedState.HasValue, "Unsized metadata should still return a sizing state.");
            Assert.IsFalse(sizedState.Value, "Unsized metadata should report false.");
            Assert.IsTrue(
                OwnerGovernedAssetLedgerService.ShouldEnforceUniqueMasterEditionForTests(sizedState),
                "Unsized collections must enforce the unique master edition guard.");
        }

        private static byte[] BuildCollectionMetadataBytes(
            bool includeCollectionDetails,
            byte? tokenStandardVariant = null,
            byte programmableConfigOption = 0,
            byte programmableConfigVariant = 0,
            byte programmableRuleSetOption = 0,
            bool encodeTokenStandardAsOption = true)
        {
            var buffer = new List<byte>();

            buffer.Add(0); // key discriminator
            buffer.AddRange(new byte[PublicKeyLength]); // update authority
            buffer.AddRange(new byte[PublicKeyLength]); // mint

            AppendBorshString(buffer, string.Empty); // name
            AppendBorshString(buffer, string.Empty); // symbol
            AppendBorshString(buffer, string.Empty); // uri

            buffer.AddRange(new byte[sizeof(ushort)]); // seller fee basis points

            buffer.Add(0); // creators flag (absent)
            buffer.AddRange(new byte[2]); // primary sale happened + is mutable flags

            buffer.Add(0); // edition nonce option flag (absent)
            if (encodeTokenStandardAsOption)
            {
                if (tokenStandardVariant.HasValue)
                {
                    buffer.Add(1); // token standard option flag (present)
                    buffer.Add(tokenStandardVariant.Value); // token standard variant
                }
                else
                {
                    buffer.Add(0); // token standard option flag (absent)
                }
            }
            else if (tokenStandardVariant.HasValue)
            {
                buffer.Add(tokenStandardVariant.Value); // token standard variant (direct encoding)
            }

            if (tokenStandardVariant.HasValue && tokenStandardVariant.Value >= 4)
            {
                buffer.Add(programmableConfigOption); // programmable config option flag

                if (programmableConfigOption == 1)
                {
                    buffer.Add(programmableConfigVariant); // programmable config variant discriminator

                    if (programmableConfigVariant == 0)
                    {
                        buffer.Add(programmableRuleSetOption); // programmable rule set option flag

                        if (programmableRuleSetOption == 1)
                        {
                            buffer.AddRange(new byte[PublicKeyLength]); // programmable rule set key placeholder
                        }
                    }
                }
            }

            buffer.Add(0); // collection flag (absent)
            buffer.Add(0); // uses flag (absent)

            buffer.Add(includeCollectionDetails ? (byte)1 : (byte)0); // collection details flag
            if (includeCollectionDetails)
            {
                buffer.Add(0); // collection details variant (V1)
                buffer.AddRange(new byte[sizeof(ulong)]); // collection size placeholder
            }

            return buffer.ToArray();
        }

        private static void AppendBorshString(List<byte> buffer, string value)
        {
            value ??= string.Empty;
            var stringBytes = Encoding.UTF8.GetBytes(value);
            var lengthBytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)stringBytes.Length);
            buffer.AddRange(lengthBytes);
            if (stringBytes.Length > 0)
            {
                buffer.AddRange(stringBytes);
            }
        }

        [TestCaseSource(nameof(MasterEditionV1ParsingTestCases))]
        public void TryParseMasterEditionLayout_MasterEditionV1(
            byte[] raw,
            ulong expectedSupply,
            byte expectedMaxSupplyOption,
            ulong? expectedMaxSupply,
            bool expectedHasMaxSupply,
            bool expectedIsUnique)
        {
            Assert.IsTrue(
                OwnerGovernedAssetLedgerService.TryParseMasterEditionLayoutForTests(
                    raw,
                    out var discriminator,
                    out var supply,
                    out var maxSupplyOption,
                    out var maxSupply));

            Assert.AreEqual(2, discriminator, "MasterEditionV1 discriminator should be parsed.");
            Assert.AreEqual(expectedSupply, supply);
            Assert.AreEqual(expectedMaxSupplyOption, maxSupplyOption);

            if (expectedMaxSupply.HasValue)
            {
                Assert.IsTrue(maxSupply.HasValue, "MaxSupply should have a value.");
                Assert.AreEqual(expectedMaxSupply.Value, maxSupply.Value);
            }
            else
            {
                Assert.IsFalse(maxSupply.HasValue, "MaxSupply should be null.");
            }

            var info = CreateCollectionMasterEditionInfo(
                discriminator,
                supply,
                maxSupplyOption,
                maxSupply);

            Assert.AreEqual(expectedHasMaxSupply, GetCollectionMasterEditionBool(info, "HasMaxSupply"));
            Assert.AreEqual(expectedIsUnique, GetCollectionMasterEditionBool(info, "IsUnique"));
        }

        [TestCaseSource(nameof(MasterEditionV2ParsingTestCases))]
        public void TryParseMasterEditionLayout_MasterEditionV2(
            byte[] raw,
            ulong expectedSupply,
            byte expectedMaxSupplyOption,
            ulong? expectedMaxSupply,
            bool expectedHasMaxSupply,
            bool expectedIsUnique)
        {
            Assert.IsTrue(
                OwnerGovernedAssetLedgerService.TryParseMasterEditionLayoutForTests(
                    raw,
                    out var discriminator,
                    out var supply,
                    out var maxSupplyOption,
                    out var maxSupply));

            Assert.AreEqual(6, discriminator, "MasterEditionV2 discriminator should be parsed.");
            Assert.AreEqual(expectedSupply, supply);
            Assert.AreEqual(expectedMaxSupplyOption, maxSupplyOption);

            if (expectedMaxSupply.HasValue)
            {
                Assert.IsTrue(maxSupply.HasValue, "MaxSupply should have a value.");
                Assert.AreEqual(expectedMaxSupply.Value, maxSupply.Value);
            }
            else
            {
                Assert.IsFalse(maxSupply.HasValue, "MaxSupply should be null.");
            }

            var info = CreateCollectionMasterEditionInfo(
                discriminator,
                supply,
                maxSupplyOption,
                maxSupply);

            Assert.AreEqual(expectedHasMaxSupply, GetCollectionMasterEditionBool(info, "HasMaxSupply"));
            Assert.AreEqual(expectedIsUnique, GetCollectionMasterEditionBool(info, "IsUnique"));
        }

        [Test]
        public void SanitizeMintCreators_PreservesVerifiedSignerMatchingPayer()
        {
            var payer = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(TokenProgram.ProgramIdKey, true, 100)
            };

            var sanitized = InvokeSanitizeMintCreators(creators, payer);

            Assert.AreEqual(1, sanitized.Count);
            Assert.IsTrue(sanitized[0].Verified);
            Assert.AreEqual(TokenProgram.ProgramIdKey, sanitized[0].Address);
        }

        [Test]
        public void SanitizeMintCreators_ThrowsWhenVerifiedCreatorDoesNotMatchPayer()
        {
            var payer = SystemProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(TokenProgram.ProgramIdKey, true, 100)
            };

            var ex = Assert.Throws<OwnerGovernedAssetLedgerException>(() => InvokeSanitizeMintCreators(creators, payer));
            StringAssert.Contains("verified", ex.UserMessage, "Exception should mention verification issue.");
        }

        private static object CreateCollectionMasterEditionInfo(
            byte discriminator,
            ulong supply,
            byte maxSupplyOption,
            ulong? maxSupply)
        {
            var nestedType = typeof(OwnerGovernedAssetLedgerService).GetNestedType(
                "CollectionMasterEditionInfo",
                BindingFlags.NonPublic);
            Assert.IsNotNull(nestedType, "Failed to locate CollectionMasterEditionInfo type.");

            var ctor = nestedType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                new[] { typeof(PublicKey), typeof(byte), typeof(ulong), typeof(byte), typeof(ulong?) },
                modifiers: null);
            Assert.IsNotNull(ctor, "Failed to locate CollectionMasterEditionInfo constructor.");

            return ctor.Invoke(new object[]
            {
                TokenProgram.ProgramIdKey,
                discriminator,
                supply,
                maxSupplyOption,
                maxSupply
            });
        }

        private static bool GetCollectionMasterEditionBool(object instance, string propertyName)
        {
            var property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, $"Failed to locate property '{propertyName}'.");
            return (bool)property.GetValue(instance);
        }

        private static byte[] BuildMasterEditionV1Bytes(ulong supply, uint option, ulong? maxSupply)
        {
            Assert.LessOrEqual(option, 1u, "V1 max supply option must be 0 or 1.");

            var maxSupplyLength = option == 1 ? sizeof(ulong) : 0;
            var buffer = new byte[1 + sizeof(ulong) + sizeof(uint) + maxSupplyLength + 2 * PublicKeyLength];
            var offset = 0;

            buffer[offset++] = 2; // MasterEditionV1 discriminator
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), supply);
            offset += sizeof(ulong);

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), option);
            offset += sizeof(uint);

            if (option == 1)
            {
                Assert.IsTrue(maxSupply.HasValue, "Max supply value required when option is 1.");
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), maxSupply.Value);
                offset += sizeof(ulong);
            }

            offset += 2 * PublicKeyLength; // printing mint + one-time printing authorization mint

            return buffer;
        }

        private static byte[] BuildMasterEditionV2Bytes(ulong supply, byte option, ulong? maxSupply)
        {
            Assert.LessOrEqual(option, (byte)1, "V2 max supply option must be 0 or 1.");

            var maxSupplyLength = option == 1 ? sizeof(ulong) : 0;
            var buffer = new byte[1 + sizeof(ulong) + sizeof(byte) + maxSupplyLength];
            var offset = 0;

            buffer[offset++] = 6; // MasterEditionV2 discriminator
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), supply);
            offset += sizeof(ulong);

            buffer[offset++] = option;

            if (option == 1)
            {
                Assert.IsTrue(maxSupply.HasValue, "Max supply value required when option is 1.");
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), maxSupply.Value);
                offset += sizeof(ulong);
            }

            return buffer;
        }

        [Test]
        public void SanitizeMintCreators_AllowsVerifiedCreatorMatchingPermittedSigner()
        {
            var payer = SystemProgram.ProgramIdKey;
            var externalSigner = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(externalSigner, true, 100)
            };

            var permittedSigners = new List<PublicKey> { payer, externalSigner };
            var sanitized = InvokeSanitizeMintCreators(creators, payer, permittedSigners);

            Assert.AreEqual(1, sanitized.Count);
            Assert.IsTrue(sanitized[0].Verified);
            Assert.AreEqual(externalSigner, sanitized[0].Address);
        }

        [Test]
        public void SanitizeMintCreators_AllowsMultipleVerifiedCreatorsMatchingPermittedSigners()
        {
            var sessionSigner = SystemProgram.ProgramIdKey;
            var externalSigner = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(sessionSigner, true, 60),
                new OwnerGovernedAssetLedgerCreator(externalSigner, true, 40)
            };

            var permittedSigners = new List<PublicKey> { sessionSigner, externalSigner };
            var sanitized = InvokeSanitizeMintCreators(creators, sessionSigner, permittedSigners);

            Assert.AreEqual(2, sanitized.Count);
            CollectionAssert.AreEquivalent(
                new[] { sessionSigner, externalSigner },
                new[] { sanitized[0].Address, sanitized[1].Address });
            Assert.IsTrue(sanitized[0].Verified);
            Assert.IsTrue(sanitized[1].Verified);
        }

        [Test]
        public void FindMissingVerifiedCreatorSignatures_ReturnsMissingAddresses()
        {
            var missingSignatureCreator = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(missingSignatureCreator, true, 100)
            };

            var transaction = new Transaction
            {
                Signatures = new List<SignaturePubKeyPair>
                {
                    new SignaturePubKeyPair
                    {
                        PublicKey = SystemProgram.ProgramIdKey,
                        Signature = Enumerable.Repeat((byte)1, 64).ToArray()
                    }
                }
            };

            var missingSigners = InvokeFindMissingVerifiedCreatorSignatures(transaction, creators);

            Assert.IsNotNull(missingSigners, "Missing signer list should not be null.");
            CollectionAssert.AreEquivalent(
                new[] { missingSignatureCreator.Key },
                missingSigners,
                "The missing signature lookup should include the creator address.");
        }

        [Test]
        public void FindMissingVerifiedCreatorSignatures_ReturnsEmptyWhenSignaturesPresent()
        {
            var creator = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(creator, true, 100)
            };

            var transaction = new Transaction
            {
                Signatures = new List<SignaturePubKeyPair>
                {
                    new SignaturePubKeyPair
                    {
                        PublicKey = creator,
                        Signature = Enumerable.Repeat((byte)1, 64).ToArray()
                    }
                }
            };

            var missingSigners = InvokeFindMissingVerifiedCreatorSignatures(transaction, creators);

            Assert.IsNotNull(missingSigners, "Missing signer list should not be null.");
            Assert.IsEmpty(missingSigners, "No creators should be flagged when all signatures are present.");
        }

        [Test]
        public void BuildMintTransactionInstructions_IncludesComputeBudgetInstructions()
        {
            var mintInstruction = new TransactionInstruction
            {
                ProgramId = SystemProgram.ProgramIdKey,
                Keys = new List<AccountMeta>(),
                Data = new byte[] { 1, 2, 3 }
            };

            const uint computeUnitLimit = 500000;
            const ulong computeUnitPrice = 25;

            var instructions = InvokeBuildMintTransactionInstructions(
                mintInstruction,
                computeUnitLimit,
                computeUnitPrice);

            Assert.AreEqual(3, instructions.Count, "Compute limit, price, and mint instructions should be present.");

            var expectedLimit = ComputeBudgetProgram.SetComputeUnitLimit(computeUnitLimit);
            var expectedPrice = ComputeBudgetProgram.SetComputeUnitPrice(computeUnitPrice);

            AssertInstructionsEqual(expectedLimit, instructions[0]);
            AssertInstructionsEqual(expectedPrice, instructions[1]);
            Assert.AreSame(mintInstruction, instructions[2], "Mint instruction should be appended last.");
        }

        [Test]
        public void BuildMintTransactionInstructions_OmitsPriceWhenUnset()
        {
            var mintInstruction = new TransactionInstruction
            {
                ProgramId = SystemProgram.ProgramIdKey,
                Keys = new List<AccountMeta>(),
                Data = new byte[] { 9, 9 }
            };

            const uint computeUnitLimit = 450000;

            var instructions = InvokeBuildMintTransactionInstructions(
                mintInstruction,
                computeUnitLimit,
                null);

            Assert.AreEqual(2, instructions.Count, "Only the compute limit and mint instructions should be present.");

            var expectedLimit = ComputeBudgetProgram.SetComputeUnitLimit(computeUnitLimit);

            AssertInstructionsEqual(expectedLimit, instructions[0]);
            Assert.AreSame(mintInstruction, instructions[1], "Mint instruction should remain last.");
        }

        [Test]
        public void BuildUpdateManifestAccountList_MatchesUpdateContextOrdering()
        {
            var owner = new Account().PublicKey;
            var config = new Account().PublicKey;
            var auth = new Account().PublicKey;
            var manifest = new Account().PublicKey;
            var mint = new Account().PublicKey;
            var ownerTokenAccount = new Account().PublicKey;
            var metadata = new Account().PublicKey;

            var method = typeof(OwnerGovernedAssetLedgerService).GetMethod(
                "BuildUpdateManifestAccountList",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Failed to locate BuildUpdateManifestAccountList via reflection.");

            var accounts = (IReadOnlyList<AccountMeta>)method.Invoke(
                null,
                new object[]
                {
                    owner,
                    config,
                    auth,
                    manifest,
                    mint,
                    ownerTokenAccount,
                    metadata
                });

            Assert.IsNotNull(accounts, "Account list should not be null.");
            Assert.AreEqual(10, accounts.Count, "update_object_manifest should pass ten accounts.");

            AssertAccountMeta(accounts[0], owner, expectedSigner: true, expectedWritable: true, index: 0);
            AssertAccountMeta(accounts[1], config, expectedSigner: false, expectedWritable: true, index: 1);
            AssertAccountMeta(accounts[2], auth, expectedSigner: false, expectedWritable: false, index: 2);
            AssertAccountMeta(accounts[3], manifest, expectedSigner: false, expectedWritable: true, index: 3);
            AssertAccountMeta(accounts[4], mint, expectedSigner: false, expectedWritable: false, index: 4);
            AssertAccountMeta(accounts[5], ownerTokenAccount, expectedSigner: false, expectedWritable: false, index: 5);
            AssertAccountMeta(accounts[6], metadata, expectedSigner: false, expectedWritable: true, index: 6);

            var metadataProgram = (PublicKey)typeof(OwnerGovernedAssetLedgerService)
                .GetField("TokenMetadataProgramId", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            var rentSysvar = (PublicKey)typeof(OwnerGovernedAssetLedgerService)
                .GetField("RentSysvarId", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            var instructionsSysvar = (PublicKey)typeof(OwnerGovernedAssetLedgerService)
                .GetField("InstructionsSysvarId", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);

            Assert.IsNotNull(metadataProgram, "Token metadata program id reflection failed.");
            Assert.IsNotNull(rentSysvar, "Rent sysvar reflection failed.");
            Assert.IsNotNull(instructionsSysvar, "Instructions sysvar reflection failed.");

            AssertAccountMeta(accounts[7], metadataProgram, expectedSigner: false, expectedWritable: false, index: 7);
            AssertAccountMeta(accounts[8], rentSysvar, expectedSigner: false, expectedWritable: false, index: 8);
            AssertAccountMeta(accounts[9], instructionsSysvar, expectedSigner: false, expectedWritable: false, index: 9);
        }

        [Test]
        public void DowngradeCreatorsForMissingSignatures_FlipsVerifiedFlag()
        {
            var missingSignatureCreator = TokenProgram.ProgramIdKey;
            var creators = new List<OwnerGovernedAssetLedgerCreator>
            {
                new OwnerGovernedAssetLedgerCreator(missingSignatureCreator, true, 60),
                new OwnerGovernedAssetLedgerCreator(SystemProgram.ProgramIdKey, true, 40)
            };

            var downgraded = InvokeDowngradeCreatorsForMissingSignatures(
                creators,
                new[] { missingSignatureCreator.Key });

            Assert.AreEqual(2, downgraded.Count, "Creator count should be preserved during downgrade.");
            Assert.IsFalse(downgraded[0].Verified, "Missing signature creator should be downgraded.");
            Assert.IsTrue(downgraded[1].Verified, "Creators with signatures should remain verified.");
        }

        [Test]
        public void IsTransportError_ReturnsTrueForSocketAndHttpExceptions()
        {
            Assert.IsTrue(InvokeIsTransportError(new SocketException()));
            Assert.IsTrue(InvokeIsTransportError(new HttpRequestException("Connection refused")));
        }

        [Test]
        public void IsTransportErrorMessage_DetectsConnectionRefusedText()
        {
            Assert.IsTrue(InvokeIsTransportErrorMessage("connection refused by host"));
            Assert.IsFalse(InvokeIsTransportErrorMessage("rpc responded with error"));
        }

        [Test]
        public void CalculateExponentialBackoffDelay_ComputesExpectedValues()
        {
            Assert.AreEqual(0, InvokeCalculateExponentialBackoffDelay(0, 500));
            Assert.AreEqual(500, InvokeCalculateExponentialBackoffDelay(1, 500));
            Assert.AreEqual(2000, InvokeCalculateExponentialBackoffDelay(3, 500));
        }

        [Test]
        public void DetermineTransportRetryDecision_CoversRetryAndFailoverBranches()
        {
            Assert.AreEqual(
                "RetryPrimary",
                InvokeDetermineTransportRetryDecision(1, 2, true, false));

            Assert.AreEqual(
                "FailoverToSecondary",
                InvokeDetermineTransportRetryDecision(3, 2, true, false));

            Assert.AreEqual(
                "Exhausted",
                InvokeDetermineTransportRetryDecision(3, 2, true, true));
        }
    }
}
#endif

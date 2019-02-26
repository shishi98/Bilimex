using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using Neo.VM;
using System;
using System.ComponentModel;
using System.Numerics;

namespace bilimex
{
    public class BrokerContract : SmartContract
    {
        public delegate object NEP5Contract(string method, object[] args);

        // Events
        [DisplayName("created")]
        public static event Action<byte[], byte[], byte[], BigInteger, byte[], BigInteger> EmitCreated; // (address, offerHash, offerAssetID, offerAmount, wantAssetID, wantAmount)

        [DisplayName("filled")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[], BigInteger, BigInteger> EmitFilled; // (address, offerHash, fillAmount, offerAssetID, offerAmount, wantAssetID, wantAmount, amountToTake)

        [DisplayName("failed")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[]> EmitFailed; // (address, offerHash, amountToTake, takerFeeAsssetID, takerFee, reason)

        [DisplayName("cancelAnnounced")]
        public static event Action<byte[], byte[]> EmitCancelAnnounced; // (address, offerHash)

        [DisplayName("cancelled")]
        public static event Action<byte[], byte[]> EmitCancelled; // (address, offerHash)

        [DisplayName("transferred")]
        public static event Action<byte[], byte[], BigInteger, byte[]> EmitTransferred; // (address, assetID, amount, reason)

        [DisplayName("deposited")]
        public static event Action<byte[], byte[], BigInteger> EmitDeposited; // (address, assetID, amount)

        [DisplayName("withdrawAnnounced")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawAnnounced; // (address, assetID, amount)

        [DisplayName("withdrawing")]
        public static event Action<byte[], byte[], BigInteger> EmitWithdrawing; // (address, assetID, amount)

        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger, byte[]> EmitWithdrawn; // (address, assetID, amount, utxoUsed)

        [DisplayName("burnt")]
        public static event Action<byte[], byte[], BigInteger> EmitBurnt; // (address, assetID, amount)

        [DisplayName("tradingFrozen")]
        public static event Action EmitTradingFrozen;

        [DisplayName("tradingResumed")]
        public static event Action EmitTradingResumed;

        [DisplayName("addedToWhitelist")]
        public static event Action<byte[], int> EmitAddedToWhitelist; // (scriptHash, whitelistEnum)

        [DisplayName("removedFromWhitelist")]
        public static event Action<byte[], int> EmitRemovedFromWhitelist; // (scriptHash, whitelistEnum)

        [DisplayName("whitelistSealed")]
        public static event Action<int> EmitWhitelistSealed; // (whitelistEnum)

        [DisplayName("arbitraryInvokeAllowed")]
        public static event Action EmitArbitraryInvokeAllowed;

        [DisplayName("feeAddressSet")]
        public static event Action<byte[]> EmitFeeAddressSet; // (address)

        [DisplayName("coordinatorSet")]
        public static event Action<byte[]> EmitCoordinatorSet; // (address)

        [DisplayName("withdrawCoordinatorSet")]
        public static event Action<byte[]> EmitWithdrawCoordinatorSet; // (address)

        [DisplayName("announceDelaySet")]
        public static event Action<BigInteger> EmitAnnounceDelaySet; // (delay)

        [DisplayName("initialized")]
        public static event Action Initialized;

        // Broker Settings & Hardcaps
        private static readonly byte[] Owner = "AZThHNqfUGV9TTPsz2i7VT69iUwfySXGW9".ToScriptHash();
        private static readonly ulong maxAnnounceDelay = 60 * 60 * 24 * 7; // 7 days

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // trading halted - only can do cancel, withdrawal & owner actions

        // Withdrawal Flags
        private static readonly byte[] Mark = { 0x50 };
        private static readonly byte[] Withdraw = { 0x51 };
        private static readonly byte[] OpCode_TailCall = { 0x69 };
        private static readonly byte Type_InvocationTransaction = 0xd1;
        private static readonly byte TAUsage_WithdrawalStage = 0xa1;
        private static readonly byte TAUsage_NEP5AssetID = 0xa2;
        private static readonly byte TAUsage_SystemAssetID = 0xa3;
        private static readonly byte TAUsage_WithdrawalAddress = 0xa4;
        private static readonly byte TAUsage_WithdrawalAmount = 0xa5;

        // Byte Constants
        private static readonly byte[] Empty = { };
        private static readonly byte[] Zero = { 0x00 };
        private static readonly byte[] NeoAssetID = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] GasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private static readonly byte[] MctAssetID = { 63, 188, 96, 124, 18, 194, 135, 54, 52, 50, 36, 164, 180, 216, 245, 19, 165, 194, 124, 168 };
        // private static readonly byte[] MctAssetID = { 161, 47, 30, 104, 24, 178, 176, 62, 14, 249, 6, 237, 39, 46, 18, 9, 144, 116, 169, 38 };
        private static readonly byte[] WithdrawArgs = { 0x00, 0xc1, 0x08, 0x77, 0x69, 0x74, 0x68, 0x64, 0x72, 0x61, 0x77 }; // PUSH0, PACK, PUSHBYTES8, "withdraw" as bytes

        // Reason Code for balance changes
        private static readonly byte[] ReasonDeposit = { 0x01 }; // Balance increased due to deposit
        private static readonly byte[] ReasonMake = { 0x02 }; // Balance reduced due to maker making
        private static readonly byte[] ReasonTake = { 0x03 }; // Balance reduced due to taker filling maker's offered asset
        private static readonly byte[] ReasonTakerFee = { 0x04 }; // Balance reduced due to taker fees
        private static readonly byte[] ReasonTakerReceive = { 0x05 }; // Balance increased due to taker receiving his cut in the trade
        private static readonly byte[] ReasonMakerReceive = { 0x06 }; // Balance increased due to maker receiving his cut in the trade
        private static readonly byte[] ReasonContractTakerFee = { 0x07 }; // Balance increased on fee address due to contract receiving taker fee
        private static readonly byte[] ReasonCancel = { 0x08 }; // Balance increased due to cancelling offer
        private static readonly byte[] ReasonPrepareWithdrawal = { 0x09 }; // Balance reduced due to preparing for asset withdrawal

        // Reason Code for fill failures
        private static readonly byte[] ReasonOfferNotExist = { 0x21 }; // Empty Offer when trying to fill
        private static readonly byte[] ReasonTakingLessThanOne = { 0x22 }; // Taking less than 1 asset when trying to fill
        private static readonly byte[] ReasonFillerSameAsMaker = { 0x23 }; // Filler same as maker
        private static readonly byte[] ReasonTakingMoreThanAvailable = { 0x24 }; // Taking more than available in the offer at the moment
        private static readonly byte[] ReasonFillingLessThanOne = { 0x25 }; // Filling less than 1 asset when trying to fill
        private static readonly byte[] ReasonNotEnoughBalanceOnFiller = { 0x26 }; // Not enough balance to give (wantAssetID) for what you want to take (offerAssetID)
        private static readonly byte[] ReasonNotEnoughBalanceOnNativeToken = { 0x27 }; // Not enough balance in native tokens to use
        private static readonly byte[] ReasonFeesMoreThanLimit = { 0x28 }; // Fees exceed 0.5%

        private struct Offer
        {
            public byte[] MakerAddress;
            public byte[] OfferAssetID;
            public BigInteger OfferAmount;
            public byte[] WantAssetID;
            public BigInteger WantAmount;
            public BigInteger AvailableAmount;
            public byte[] Nonce;
        }

        private static Offer NewOffer(
            byte[] makerAddress,
            byte[] offerAssetID, byte[] offerAmount,
            byte[] wantAssetID, byte[] wantAmount,
            byte[] nonce
        )
        {
            return new Offer
            {
                MakerAddress = makerAddress.Take(20),
                OfferAssetID = offerAssetID,
                OfferAmount = offerAmount.AsBigInteger(),
                WantAssetID = wantAssetID,
                WantAmount = wantAmount.AsBigInteger(),
                AvailableAmount = offerAmount.AsBigInteger(),
                Nonce = nonce,
            };
        }

        private struct WithdrawInfo
        {
            public BigInteger TimeStamp;
            public BigInteger Amount;
        }

        /// <summary>
        ///   This is the Switcheo smart contract entrypoint.
        /// 
        ///   Parameter List: 0710
        ///   Return List: 05
        /// </summary>
        /// <param name="operation">
        ///   The method to be invoked.
        /// </param>
        /// <param name="args">
        ///   Input parameters for the delegated method.
        /// </param>
        public static object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                if (GetState() == Pending) return false;

                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var withdrawalStage = GetWithdrawalStage(currentTxn);
                var withdrawingAddr = GetWithdrawalAddress(currentTxn);
                var assetID = GetWithdrawalAsset(currentTxn);
                var isWithdrawingNEP5 = assetID.Length == 20;
                var inputs = currentTxn.GetInputs();
                var outputs = currentTxn.GetOutputs();
                var references = currentTxn.GetReferences();

                // Check that Application trigger will be tail called with the correct params
                if (currentTxn.Type != Type_InvocationTransaction) return false;
                if (((InvocationTransaction)currentTxn).Script != WithdrawArgs.Concat(OpCode_TailCall).Concat(ExecutionEngine.ExecutingScriptHash)) return false;

                if (withdrawalStage == Mark)
                {
                    // Check that inputs are not already reserved (We must not re-use a UTXO that is already reserved)
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), WithdrawalAddressForTransactionKey(i.PrevHash)).Length > 0 && i.PrevIndex == 0) return false;
                    }

                    // Check that there is at most 2 outputs (the withdrawing output and the change)
                    if (outputs.Length > 2) return false;

                    // Check amount > 0, balance enough and whether there is an existing withdraw as withdraw can only happen 1 at a time for each asset
                    var amount = isWithdrawingNEP5 ? GetWithdrawalAmount(currentTxn) : outputs[0].Value;
                    if (!VerifyWithdrawalValid(withdrawingAddr, assetID, amount)) return false;

                    // Check that the transaction is signed by the withdraw coordinator
                    if (!Runtime.CheckWitness(GetWithdrawCoordinatorAddress()))
                    {
                        // If not signed by withdraw coordinator it must be pre-announced + signed by the user
                        if (!Runtime.CheckWitness(withdrawingAddr)) return false;
                        if (!IsWithdrawalAnnounced(withdrawingAddr, assetID, amount)) return false;
                    }
                    
                    // Check inputs and outputs make sense for NEP-5 withdrawals or System Asset withdrawals
                    if (isWithdrawingNEP5)
                    {
                        // Accept only 1 input so we check for only 1 input
                        if (references.Length != 1) return false;
                        // Check that NEP5 withdrawals don't use contract's system assets
                        if (references[0].ScriptHash == ExecutionEngine.ExecutingScriptHash) return false;
                    }
                    else
                    {
                        // Initialize totals for burn check
                        ulong totalIn = 0;
                        ulong totalOut = 0;
                        // Check that all inputs are from contract
                        foreach (var i in references)
                        {
                            totalIn += (ulong)i.Value;
                            if (i.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                        }
                        // Check that outputs are a valid self-send for system asset withdrawals
                        // (In marking phase, all assets from contract should be sent to contract and nothing should go anywhere else)
                        foreach (var o in outputs)
                        {
                            totalOut += (ulong)o.Value;
                            if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                            if (o.AssetId != assetID) return false;
                        }
                        // Check that only required inputs are used (if deleting the last input causes the totalIn to still be > amount that means the last input was useless and is wasting UTXOs)
                        if (totalIn - (ulong)references[inputs.Length - 1].Value > amount) return false;
                        // Ensure that nothing is burnt
                        if (totalIn != totalOut) return false;
                    }

                    return true;
                }
                else if (withdrawalStage == Withdraw)
                {
                    // Check that there is only the neccessary inputs/outputs
                    if (inputs.Length != 1) return false;
                    if (outputs.Length != 1) return false;
                    
                    if (isWithdrawingNEP5)
                    {
                        // Check that NEP5 withdrawals don't use contract assets
                        if (references[0].ScriptHash == ExecutionEngine.ExecutingScriptHash) return false;
                        // Check for whitelist if we are doing old style NEP-5 transfers;
                        // New style NEP-5 transfers SHOULD NOT require contract verification / witness
                        if (!IsWhitelistedOldNEP5(assetID)) return false;
                    }
                    else
                    {
                        // Check that UTXO is from a reserved txn
                        if (Storage.Get(Context(), WithdrawalAddressForTransactionKey(inputs[0].PrevHash)) != withdrawingAddr) return false;
                        // Check that the correct UTXO of the txn is used
                        if (inputs[0].PrevIndex != 0) return false;

                        // Check withdrawal destination
                        if (outputs[0].AssetId != assetID) return false;
                        if (outputs[0].ScriptHash != withdrawingAddr) return false;
                    }

                    return true;
                }
                return false;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                // == Init ==
                if (operation == "initialize")
                {
                    if (!Runtime.CheckWitness(Owner))
                    {
                        Runtime.Log("Owner signature verification failed!");
                        return false;
                    }
                    if (args.Length != 3) return false;
                    return Initialize((byte[])args[0], (byte[])args[1], (byte[])args[2]);
                }

                // == Getters ==
                if (operation == "getState") return GetState();
                if (operation == "getOffer") return GetOffer((byte[])args[0]);
                if (operation == "getBalance") return GetBalance((byte[])args[0], (byte[])args[1]);
                if (operation == "getIsWhitelisted") return GetIsWhitelisted((byte[])args[0], (int)args[1]);  // (assetID, whitelistEnum)
                if (operation == "getFeeAddress") return GetFeeAddress();
                if (operation == "getCoordinatorAddress") return GetCoordinatorAddress();
                if (operation == "getWithdrawCoordinatorAddress") return GetWithdrawCoordinatorAddress();
                if (operation == "getAnnounceDelay") return GetAnnounceDelay();
                if (operation == "getAnnouncedWithdraw") return GetAnnouncedWithdraw((byte[])args[0], (byte[])args[1]);  // (originator, assetID)
                if (operation == "getAnnouncedCancel") return GetAnnouncedCancel((byte[])args[0]);  // (offerHash)

                // == Execute == 
                if (operation == "deposit") // (originator, assetID, amount)
                {
                    if (args.Length != 3) return false;
                    return Deposit((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                }
                if (operation == "depositFrom") // (originator, assetID, amount)
                {
                    if (args.Length != 3) return false;
                    return DepositFrom((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                }
                if (operation == "onTokenTransfer") // deposit for MCT contract only (from, to, amount)
                {
                    if (args.Length != 3) return false;
                    if (ExecutionEngine.CallingScriptHash != MctAssetID) return false;
                    if (ExecutionEngine.ExecutingScriptHash != (byte[])args[1]) return false;
                    if (!ReceivedNEP5((byte[])args[0], MctAssetID, (BigInteger)args[2])) throw new Exception("ReceivedNEP5 onTransfer failed!");
                    return true;
                }
                if (operation == "makeOffer") // (makerAddress, offerAssetID, offerAmount, wantAssetID, wantAmount, nonce)
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 6) return false;
                    var offer = NewOffer((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[5]);
                    return MakeOffer(offer);
                }
                if (operation == "fillOffer") // fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount)
                {
                    if (GetState() != Active) return false;
                    if (args.Length != 6) return false;
                    return FillOffer((byte[])args[0], (byte[])args[1], (BigInteger)args[2], (byte[])args[3], (BigInteger)args[4], (bool)args[5]);
                }
                if (operation == "cancelOffer") // (offerHash)
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 1) return false;
                    return CancelOffer((byte[])args[0]);
                }
                if (operation == "withdraw") // ()
                {
                    if (GetState() == Pending) return false;
                    return ProcessWithdrawal();
                }
                if (operation == "announceCancel") // (offerHash)
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 1) return false;
                    return AnnounceCancel((byte[])args[0]);
                }
                if (operation == "announceWithdraw") // (originator, assetID, amountToWithdraw)
                {
                    if (GetState() == Pending) return false;
                    if (args.Length != 3) return false;
                    return AnnounceWithdraw((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                }

                // == Owner ==
                if (!Runtime.CheckWitness(Owner))
                {
                    Runtime.Log("Owner signature verification failed");
                    return false;
                }
                if (operation == "freezeTrading")
                {
                    if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false; // ensure both coordinator and owner signed
                    Storage.Put(Context(), "state", Inactive);
                    EmitTradingFrozen();
                    return true;
                }
                if (operation == "unfreezeTrading")
                {
                    if (!Runtime.CheckWitness(GetCoordinatorAddress())) return false; // ensure both coordinator and owner signed
                    Storage.Put(Context(), "state", Active);
                    EmitTradingResumed();
                    return true;
                }
                if (operation == "setAnnounceDelay")
                {
                    if (args.Length != 1) return false;
                    return SetAnnounceDelay((BigInteger)args[0]); ;
                }
                if (operation == "setCoordinatorAddress")
                {
                    if (args.Length != 1) return false;
                    return SetCoordinatorAddress((byte[])args[0]); ;
                }
                if (operation == "setWithdrawCoordinatorAddress")
                {
                    if (args.Length != 1) return false;
                    return SetWithdrawCoordinatorAddress((byte[])args[0]); ;
                }
                if (operation == "setFeeAddress")
                {
                    if (args.Length != 1) return false;
                    return SetFeeAddress((byte[])args[0]);
                }
                if (operation == "addToWhitelist")
                {
                    if (args.Length != 2) return false;
                    return AddToWhitelist((byte[])args[0], (int)args[1]);
                }
                if (operation == "removeFromWhitelist")
                {
                    if (args.Length != 2) return false;
                    return RemoveFromWhitelist((byte[])args[0], (int)args[1]);
                }
                if (operation == "sealWhitelist")
                {
                    if (args.Length != 1) return false;
                    return SealWhitelist((int)args[0]);
                }
                if (operation == "setArbitraryInvokeAllowed")
                {
                    Storage.Put(Context(), "arbitraryInvokeAllowed", Active);
                    EmitArbitraryInvokeAllowed();
                    return true;
                }
            }

            return true;
        }

        /***********
         * Getters *
         ***********/

        private static byte[] GetState()
        {
            return Storage.Get(Context(), "state");
        }

        private static BigInteger GetBalance(byte[] address, byte[] assetID)
        {
            if (address.Length != 20) throw new ArgumentOutOfRangeException();
            if (assetID.Length != 20 && assetID.Length != 32) throw new ArgumentOutOfRangeException();
            return Storage.Get(Context(), BalanceKey(address, assetID)).AsBigInteger();
        }

        private static bool GetIsWhitelisted(byte[] assetID, int whitelistEnum)
        {
            if (assetID.Length != 20) throw new ArgumentOutOfRangeException();
            return Storage.Get(Context(), GetWhitelistKey(assetID, whitelistEnum)).Length > 0;
        }

        private static BigInteger GetAnnounceDelay()
        {
            return Storage.Get(Context(), "announceDelay").AsBigInteger();
        }

        private static byte[] GetFeeAddress()
        {
            return Storage.Get(Context(), "feeAddress");
        }

        private static byte[] GetCoordinatorAddress()
        {
            return Storage.Get(Context(), "coordinatorAddress");
        }

        private static byte[] GetWithdrawCoordinatorAddress()
        {
            return Storage.Get(Context(), "withdrawCoordinatorAddress");
        }

        private static Offer GetOffer(byte[] offerHash)
        {
            if (offerHash.Length != 32) throw new ArgumentOutOfRangeException();

            byte[] offerData = Storage.Get(Context(), OfferKey(offerHash));
            if (offerData.Length == 0) return new Offer();

            return (Offer)offerData.Deserialize();
        }

        private static WithdrawInfo GetAnnouncedWithdraw(byte[] withdrawingAddr, byte[] assetID)
        {
            var announce = Storage.Get(Context(), WithdrawAnnounceKey(withdrawingAddr, assetID));
            if (announce.Length == 0) return new WithdrawInfo(); // not announced
            return (WithdrawInfo)announce.Deserialize();
        }

        private static BigInteger GetAnnouncedCancel(byte[] offerHash)
        {
            var announceTime = Storage.Get(Context(), CancelAnnounceKey(offerHash));
            if (announceTime.Length == 0) return -1; // not announced
            return announceTime.AsBigInteger();
        }

        /***********
         * Control *
         ***********/

        private static bool Initialize(byte[] feeAddress, byte[] coordinatorAddress, byte[] withdrawCoordinatorAddress)
        {
            if (GetState() != Pending) return false;

            if (!SetFeeAddress(feeAddress)) throw new Exception("Failed to set fee address");
            if (!SetCoordinatorAddress(coordinatorAddress)) throw new Exception("Failed to set the coordinator");
            if (!SetWithdrawCoordinatorAddress(withdrawCoordinatorAddress)) throw new Exception("Failed to set the withdrawCoordinator");
            if (!SetAnnounceDelay(maxAnnounceDelay)) throw new Exception("Failed to announcement delay");

            Storage.Put(Context(), "state", Active);
            Initialized();

            return true;
        }

        private static bool SetFeeAddress(byte[] feeAddress)
        {
            if (feeAddress.Length != 20) return false;
            Storage.Put(Context(), "feeAddress", feeAddress);
            EmitFeeAddressSet(feeAddress);
            return true;
        }

        private static bool SetCoordinatorAddress(byte[] coordinatorAddress)
        {
            if (coordinatorAddress.Length != 20) return false;
            Storage.Put(Context(), "coordinatorAddress", coordinatorAddress);
            EmitCoordinatorSet(coordinatorAddress);
            return true;
        }

        private static bool SetWithdrawCoordinatorAddress(byte[] withdrawCoordinatorAddress)
        {
            if (withdrawCoordinatorAddress.Length != 20) return false;
            Storage.Put(Context(), "withdrawCoordinatorAddress", withdrawCoordinatorAddress);
            EmitWithdrawCoordinatorSet(withdrawCoordinatorAddress);
            return true;
        }

        private static bool SetAnnounceDelay(BigInteger delay)
        {
            if (delay < 0 || delay > maxAnnounceDelay) return false;
            Storage.Put(Context(), "announceDelay", delay);
            EmitAnnounceDelaySet(delay);
            return true;
        }

        private static bool AddToWhitelist(byte[] scriptHash, int whitelistEnum)
        {
            if (scriptHash.Length != 20) return false;
            if (IsWhitelistSealed(whitelistEnum)) return false;
            var key = GetWhitelistKey(scriptHash, whitelistEnum);
            Storage.Put(Context(), key, Active);
            EmitAddedToWhitelist(scriptHash, whitelistEnum);
            return true;
        }

        private static bool RemoveFromWhitelist(byte[] scriptHash, int whitelistEnum)
        {
            if (scriptHash.Length != 20) return false;
            if (IsWhitelistSealed(whitelistEnum)) return false;
            var key = GetWhitelistKey(scriptHash, whitelistEnum);
            Storage.Delete(Context(), key);
            EmitRemovedFromWhitelist(scriptHash, whitelistEnum);
            return true;
        }

        private static bool SealWhitelist(int whitelistEnum)
        {
            Storage.Put(Context(), GetWhitelistSealedKey(whitelistEnum), Active);
            EmitWhitelistSealed(whitelistEnum);
            return true;
        }

        /***********
         * Trading *
         ***********/

        private static bool MakeOffer(Offer offer)
        {
            // Check that transaction is signed by the maker and coordinator
            if (!CheckTradeWitnesses(offer.MakerAddress)) return false;

            // Check that nonce is not repeated
            var offerHash = Hash(offer);
            if (Storage.Get(Context(), OfferKey(offerHash)) != Empty) return false;

            // Check that the amounts > 0
            if (!(offer.OfferAmount > 0 && offer.WantAmount > 0)) return false;

            // Check the trade is across different assets
            if (offer.OfferAssetID == offer.WantAssetID) return false;

            // Check that asset IDs are valid
            if ((offer.OfferAssetID.Length != 20 && offer.OfferAssetID.Length != 32) ||
                (offer.WantAssetID.Length != 20 && offer.WantAssetID.Length != 32)) return false;

            // Reduce available balance for the offered asset and amount
            if (!ReduceBalance(offer.MakerAddress, offer.OfferAssetID, offer.OfferAmount, ReasonMake)) return false;

            // Add the offer to storage
            StoreOffer(offerHash, offer);

            // Notify clients
            EmitCreated(offer.MakerAddress, offerHash, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount);
            return true;
        }

        // Fills an offer by taking the amount you want
        // => amountToFill's asset type = offer's wantAssetID
        // amountToTake's asset type = offerAssetID (taker is taking what is offered)
        private static bool FillOffer(byte[] fillerAddress, byte[] offerHash, BigInteger amountToTake, byte[] takerFeeAssetID, BigInteger takerFeeAmount, bool burnTokens)
        {
            // Note: We do all checks first then execute state changes

            // Check that transaction is signed by the filler and coordinator
            if (!CheckTradeWitnesses(fillerAddress)) return false;

            // Check fees
            if (takerFeeAssetID.Length != 20 && takerFeeAssetID.Length != 32) return false;
            if (takerFeeAmount < 0) return false;

            // Check that the offer still exists 
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonOfferNotExist);
                return false;
            }

            // Check that the filler is different from the maker
            if (fillerAddress == offer.MakerAddress)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFillerSameAsMaker);
                return false;
            }

            // Check that the amount that will be taken is at least 1
            if (amountToTake < 1)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonTakingLessThanOne);
                return false;
            }

            // Check that you cannot take more than available
            if (amountToTake > offer.AvailableAmount)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonTakingMoreThanAvailable);
                return false;
            }

            // Calculate amount we have to give the offerer (what the offerer wants)
            BigInteger amountToFill = (amountToTake * offer.WantAmount) / offer.OfferAmount;

            // Check that amount to fill(give) is not less than 1
            if (amountToFill < 1)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFillingLessThanOne);
                return false;
            }

            // Check that there is enough balance to reduce for filler
            var fillerBalance = GetBalance(fillerAddress, offer.WantAssetID);
            if (fillerBalance < amountToFill)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonNotEnoughBalanceOnFiller);
                return false;
            }

            // Check if we should deduct fees separately from the taker amount
            bool deductFeesSeparately = takerFeeAssetID != offer.OfferAssetID;

            // Check that there is enough balance in native fees if using native fees
            if (deductFeesSeparately && GetBalance(fillerAddress, takerFeeAssetID) < takerFeeAmount)
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonNotEnoughBalanceOnNativeToken);
                return false;
            }

            // Check that the amountToTake is not more than 0.5% if not using native fees
            if (!deductFeesSeparately && ((takerFeeAmount * 1000) / amountToTake > 5))
            {
                EmitFailed(fillerAddress, offerHash, amountToTake, takerFeeAssetID, takerFeeAmount, ReasonFeesMoreThanLimit);
                return false;
            }

            // Reduce balance from filler
            ReduceBalance(fillerAddress, offer.WantAssetID, amountToFill, ReasonTake);

            // Move filled asset to the maker balance
            IncreaseBalance(offer.MakerAddress, offer.WantAssetID, amountToFill, ReasonMakerReceive);

            // Move taken asset to the taker balance
            var amountToTakeAfterFees = deductFeesSeparately ? amountToTake : amountToTake - takerFeeAmount;
            IncreaseBalance(fillerAddress, offer.OfferAssetID, amountToTakeAfterFees, ReasonTakerReceive);

            // Move fees
            byte[] feeAddress = Storage.Get(Context(), "feeAddress");
            if (takerFeeAmount > 0)
            {
                if (deductFeesSeparately)
                {
                    // Reduce fees here separately as it is a different asset type
                    ReduceBalance(fillerAddress, takerFeeAssetID, takerFeeAmount, ReasonTakerFee);
                }
                if (!burnTokens)
                {
                    // Only increase fee address balance if not burning
                    IncreaseBalance(feeAddress, takerFeeAssetID, takerFeeAmount, ReasonContractTakerFee);
                } else
                {
                    // Emit burnt event for easier client tracking
                    EmitBurnt(fillerAddress, takerFeeAssetID, takerFeeAmount);
                }
            }

            // Update available amount
            offer.AvailableAmount = offer.AvailableAmount - amountToTake;

            // Store updated offer
            StoreOffer(offerHash, offer);

            // Notify clients
            EmitFilled(fillerAddress, offerHash, amountToFill, offer.OfferAssetID, offer.OfferAmount, offer.WantAssetID, offer.WantAmount, amountToTake);

            return true;
        }

        private static bool CancelOffer(byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that the transaction is signed by the coordinator or pre-announced
            var cancellationAnnounced = IsCancellationAnnounced(offerHash);
            var coordinatorWitnessed = Runtime.CheckWitness(GetCoordinatorAddress());
            if (!(coordinatorWitnessed || cancellationAnnounced)) return false;

            // Check that transaction is signed by the canceller or trading is frozen
            if (!(Runtime.CheckWitness(offer.MakerAddress) || (IsTradingFrozen() && coordinatorWitnessed))) return false;

            // Move funds to maker address
            IncreaseBalance(offer.MakerAddress, offer.OfferAssetID, offer.AvailableAmount, ReasonCancel);

            // Remove offer
            RemoveOffer(offerHash);

            // Clean up announcement
            var key = CancelAnnounceKey(offerHash);
            if (key.Length > 0)
            {
                Storage.Delete(Context(), key);
            }

            // Notify runtime
            EmitCancelled(offer.MakerAddress, offerHash);
            return true;
        }

        private static bool AnnounceCancel(byte[] offerHash)
        {
            // Check that the offer exists
            Offer offer = GetOffer(offerHash);
            if (offer.MakerAddress == Empty) return false;

            // Check that transaction is signed by the canceller or trading is frozen 
            if (!Runtime.CheckWitness(offer.MakerAddress)) return false;

            Storage.Put(Context(), CancelAnnounceKey(offerHash), Runtime.Time);
            
            // Announce cancel intent to coordinator
            EmitCancelAnnounced(offer.MakerAddress, offerHash);

            return true;
        }

        private static void StoreOffer(byte[] offerHash, Offer offer)
        {
            // Remove offer if completely filled
            if (offer.AvailableAmount == 0)
            {
                RemoveOffer(offerHash);
            }
            else if (offer.AvailableAmount < 0)
            {
                throw new Exception("Invalid offer available amount!");
            }
            // Store offer otherwise
            else
            {
                // Serialize offer
                var offerData = offer.Serialize();
                Storage.Put(Context(), OfferKey(offerHash), offerData);
            }
        }

        private static void RemoveOffer(byte[] offerHash)
        {
            // Delete offer data
            Storage.Delete(Context(), OfferKey(offerHash));
        }

        private static bool IncreaseBalance(byte[] originator, byte[] assetID, BigInteger amount, byte[] reason)
        {
            if (amount < 1) throw new ArgumentOutOfRangeException();

            byte[] key = BalanceKey(originator, assetID);
            BigInteger currentBalance = Storage.Get(Context(), key).AsBigInteger();
            Storage.Put(Context(), key, currentBalance + amount);
            EmitTransferred(originator, assetID, amount, reason);

            return true;
        }

        private static bool ReduceBalance(byte[] address, byte[] assetID, BigInteger amount, byte[] reason)
        {
            if (amount < 1) throw new ArgumentOutOfRangeException();

            var key = BalanceKey(address, assetID);
            var currentBalance = Storage.Get(Context(), key).AsBigInteger();
            var newBalance = currentBalance - amount;

            if (newBalance < 0) return false;

            if (newBalance > 0) Storage.Put(Context(), key, newBalance);
            else Storage.Delete(Context(), key);
            EmitTransferred(address, assetID, 0 - amount, reason);

            return true;
        }

        /***********
         * Deposit *
         ***********/

        private static bool Deposit(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Check asset lengths
            if (assetID.Length == 32)
            {
                // Accept all system assets
                var received = Received();

                // Mark deposit
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                Storage.Put(Context(), DepositKey(currentTxn), 1);
                return received;
            }
            else if (assetID.Length == 20)
            {
                // Update balances first
                if (!ReceivedNEP5(originator, assetID, amount)) return false;

                // Execute deposit to our contract (ExecutionEngine.ExecutingScriptHash)
                TransferNEP5(originator, ExecutionEngine.ExecutingScriptHash, assetID, amount);

                return true;
            }

            // Unknown asset category
            return false;
        }

        private static bool DepositFrom(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Check asset length
            if (assetID.Length != 20) return false;

            // Update balances first
            if (!ReceivedNEP5(originator, assetID, amount)) return false;

            // Execute deposit to our contract (ExecutionEngine.ExecutingScriptHash)
            TransferFromNEP5(originator, ExecutionEngine.ExecutingScriptHash, assetID, amount);

            return true;
        }

        // Received NEP-5 tokens
        private static bool ReceivedNEP5(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Verify that deposit is authorized
            if (!CheckTradeWitnesses(originator)) return false;
            if (GetState() != Active) return false;

            // Check that the contract is safe
            if (!(IsWhitelistedOldNEP5(assetID) || IsWhitelistedNewNEP5(assetID) || IsArbitraryInvokeAllowed())) return false;

            // Check address and amounts
            if (originator.Length != 20) return false;
            if (amount < 1) return false;

            // Update balances first
            IncreaseBalance(originator, assetID, amount, ReasonDeposit);
            EmitDeposited(originator, assetID, amount);

            return true;
        }

        // Received system asset
        public static bool Received()
        {
            // Check the current transaction for the system assets
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var outputs = currentTxn.GetOutputs();
            var references = currentTxn.GetReferences();

            // Check if existing deposit flag is present
            if (Storage.Get(Context(), DepositKey(currentTxn)).Length > 0) return false;

            // Don't deposit if this is a withdrawal
            var coordinatorAddress = GetCoordinatorAddress();
            var withdrawCoordinatorAddress = GetWithdrawCoordinatorAddress();
            foreach (var i in references)
            {
                if (i.ScriptHash == ExecutionEngine.ExecutingScriptHash) return false;
                if (i.ScriptHash == coordinatorAddress) return false;
                if (i.ScriptHash == withdrawCoordinatorAddress) return false;
            }

            // Only deposit those assets not from contract
            ulong sentGasAmount = 0;
            ulong sentNeoAmount = 0;

            foreach (var o in outputs)
            {
                if (o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                {
                    if (o.AssetId == GasAssetID)
                    {
                        sentGasAmount += (ulong)o.Value;
                    }
                    else if (o.AssetId == NeoAssetID)
                    {
                        sentNeoAmount += (ulong)o.Value;
                    }
                }
            }

            byte[] firstAvailableAddress = references[0].ScriptHash;
            if (sentGasAmount > 0)
            {
                IncreaseBalance(firstAvailableAddress, GasAssetID, sentGasAmount, ReasonDeposit);
                EmitDeposited(firstAvailableAddress, GasAssetID, sentGasAmount);
            }
            if (sentNeoAmount > 0)
            {
                IncreaseBalance(firstAvailableAddress, NeoAssetID, sentNeoAmount, ReasonDeposit);
                EmitDeposited(firstAvailableAddress, NeoAssetID, sentNeoAmount);
            }

            return true;
        }

        /**************
         * Withdrawal *
         **************/

        private static bool VerifyWithdrawalValid(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            if (amount < 1) return false;

            var balance = GetBalance(holderAddress, assetID);
            if (balance < amount) return false;

            var withdrawingAmount = GetWithdrawingAmount(holderAddress, assetID);
            if (withdrawingAmount > 0) return false;

            return true;
        }

        private static bool AnnounceWithdraw(byte[] originator, byte[] assetID, BigInteger amountToWithdraw)
        {
            if (!Runtime.CheckWitness(originator)) return false;

            if (!VerifyWithdrawalValid(originator, assetID, amountToWithdraw)) return false;

            WithdrawInfo withdrawInfo = new WithdrawInfo
            {
                TimeStamp = Runtime.Time,
                Amount = amountToWithdraw
            };

            var key = WithdrawAnnounceKey(originator, assetID);
            Storage.Put(Context(), key, withdrawInfo.Serialize());

            // Announce withdrawal intent to clients
            EmitWithdrawAnnounced(originator, assetID, amountToWithdraw);

            return true;
        }

        private static object ProcessWithdrawal()
        {
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var withdrawalStage = GetWithdrawalStage(currentTxn);
            var withdrawingAddr = GetWithdrawalAddress(currentTxn); // Not validated, anyone can help anyone do step 2 of withdrawal
            var assetID = GetWithdrawalAsset(currentTxn);
            var isWithdrawingNEP5 = assetID.Length == 20;
            var inputs = currentTxn.GetInputs();
            var outputs = currentTxn.GetOutputs();
            
            if (withdrawalStage == Mark)
            {
                if (!Runtime.CheckWitness(ExecutionEngine.ExecutingScriptHash)) return false;
                BigInteger amount = isWithdrawingNEP5 ? GetWithdrawalAmount(currentTxn) : outputs[0].Value;

                bool withdrawalAnnounced = IsWithdrawalAnnounced(withdrawingAddr, assetID, amount);

                // Only pass if withdraw coordinator signed or withdrawal is announced
                if (!(Runtime.CheckWitness(GetWithdrawCoordinatorAddress()) || withdrawalAnnounced))
                {
                    Runtime.Log("Withdraw coordinator witness missing or withdrawal unannounced");
                    return false;
                }

                // Check again that: amount > 0, balance enough and whether there is an existing withdraw as withdraw can only happen 1 at a time for each asset
                // Because things might have changed between verification phase and application phase
                if (!VerifyWithdrawalValid(withdrawingAddr, assetID, amount))
                {
                    Runtime.Log("Verify withdrawal failed");
                    return false;
                }

                // Attempt to reduce the balance
                if (!ReduceBalance(withdrawingAddr, assetID, amount, ReasonPrepareWithdrawal))
                {
                    Runtime.Log("Reduce balance for withdrawal failed");
                    return false;
                }

                // Clear withdrawing announcement by user for this asset if any withdrawal is successful because it would mean that withdrawal is working correctly and exchange is in action
                var key = WithdrawAnnounceKey(withdrawingAddr, assetID);
                if (key.Length > 0)
                {
                    Storage.Delete(Context(), key);
                }

                // Reserve the transaction hash for the user if withdrawing system assets
                if (!isWithdrawingNEP5)
                {
                    Storage.Put(Context(), WithdrawalAddressForTransactionKey(currentTxn.Hash), withdrawingAddr);
                }

                // Save withdrawing assetID and amount for user to be used later
                Storage.Put(Context(), WithdrawingKey(withdrawingAddr, assetID), amount);
                EmitWithdrawing(withdrawingAddr, assetID, amount);

                return true;
            }
            else if (withdrawalStage == Withdraw)
            {
                var amount = GetWithdrawingAmount(withdrawingAddr, assetID);

                if (isWithdrawingNEP5)
                {
                    // Check if already withdrawn for NEP-5 only as no double withdraw of system assets are secured by reserved utxos
                    if (amount <= 0) return false;

                    // Check old whitelist
                    if (IsWhitelistedOldNEP5(assetID))
                    {
                        // This contract must pass witness for old NEP-5 transfer to succeed
                        if (!Runtime.CheckWitness(ExecutionEngine.ExecutingScriptHash)) return false;
                    }
                    // Check new whitelist
                    else
                    {
                        // New-style NEP-5 transfers or arbitrary invokes SHOULD NOT pass this contract's witness checks
                        if (Runtime.CheckWitness(ExecutionEngine.ExecutingScriptHash)) return false;
                        // Allow only if in whitelist or arbitrary dynamic invoke is allowed
                        if (!(IsWhitelistedNewNEP5(assetID) || IsArbitraryInvokeAllowed())) return false;
                    }
                    // Execute withdrawal
                    Storage.Delete(Context(), WithdrawingKey(withdrawingAddr, assetID));
                    EmitWithdrawn(withdrawingAddr, assetID, amount, inputs[0].PrevHash);
                    TransferNEP5(ExecutionEngine.ExecutingScriptHash, withdrawingAddr, assetID, amount);
                }
                else
                {
                    // Execute withdrawal
                    Storage.Delete(Context(), WithdrawingKey(withdrawingAddr, assetID));
                    EmitWithdrawn(withdrawingAddr, assetID, amount, inputs[0].PrevHash);
                    // Clean up reservations
                    var key = WithdrawalAddressForTransactionKey(inputs[0].PrevHash);
                    Storage.Delete(Context(), key);
                }

                return true;
            }

            return false;
        }

        private static BigInteger GetWithdrawingAmount(byte[] originator, byte[] assetID)
        {
            return Storage.Get(Context(), WithdrawingKey(originator, assetID)).AsBigInteger();
        }

        private static byte[] GetWithdrawalAddress(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalAddress) return attr.Data.Take(20);
            }
            throw new ArgumentNullException();
        }

        private static byte[] GetWithdrawalAsset(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_NEP5AssetID) return attr.Data.Take(20);
                if (attr.Usage == TAUsage_SystemAssetID) return attr.Data.Take(32);
            }
            throw new ArgumentNullException();
        }

        private static BigInteger GetWithdrawalAmount(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalAmount) return attr.Data.Take(32).Concat(Zero).AsBigInteger();
            }
            throw new ArgumentNullException();
        }

        private static byte[] GetWithdrawalStage(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == TAUsage_WithdrawalStage) return attr.Data.Take(1);
            }
            throw new ArgumentNullException();
        }

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;
        private static BigInteger AmountToOffer(Offer o, BigInteger amount) => (o.OfferAmount * amount) / o.WantAmount;
        private static bool IsTradingFrozen() => Storage.Get(Context(), "state") == Inactive;
        private static bool IsArbitraryInvokeAllowed() => Storage.Get(Context(), "arbitraryInvokeAllowed") == Active;

        private static bool IsWithdrawalAnnounced(byte[] withdrawingAddr, byte[] assetID, BigInteger amount)
        {
            var announce = Storage.Get(Context(), WithdrawAnnounceKey(withdrawingAddr, assetID));
            if (announce.Length == 0) return false; // not announced
            var announceInfo = (WithdrawInfo)announce.Deserialize();
            var announceDelay = GetAnnounceDelay();

            return announceInfo.TimeStamp + announceDelay < Runtime.Time && announceInfo.Amount == amount;
        }

        private static bool IsCancellationAnnounced(byte[] offerHash)
        {
            var announceTime = Storage.Get(Context(), CancelAnnounceKey(offerHash));
            if (announceTime.Length == 0) return false; // not announced
            var announceDelay = GetAnnounceDelay();

            return announceTime.AsBigInteger() + announceDelay < Runtime.Time;
        }

        private static bool IsWhitelistedOldNEP5(byte[] assetID)
        {
            if (assetID.Length != 20) return false;
            if (assetID.AsBigInteger() == 0) return false;
            return Storage.Get(Context(), OldWhitelistKey(assetID)).Length > 0;
        }

        private static bool IsWhitelistedNewNEP5(byte[] assetID)
        {
            if (assetID.Length != 20) return false;
            if (assetID.AsBigInteger() == 0) return false;
            return Storage.Get(Context(), NewWhitelistKey(assetID)).Length > 0;
        }

        private static bool IsPythonNEP5(byte[] assetID)
        {
            if (assetID.Length != 20) return false;
            if (assetID.AsBigInteger() == 0) return false;
            return Storage.Get(Context(), PytWhitelistKey(assetID)).Length > 0;
        }

        private static bool IsWhitelistSealed(int whitelistEnum)
        {
            return Storage.Get(Context(), GetWhitelistSealedKey(whitelistEnum)).Length > 0;
        }

        private static bool CheckTradeWitnesses(byte[] traderAddress)
        {
            // Cache coordinator address for checks
            var coordinatorAddress = GetCoordinatorAddress();

            // Check that transaction is signed by the trader
            if (!Runtime.CheckWitness(traderAddress)) return false;

            // Check that transaction is signed by the coordinator
            if (!Runtime.CheckWitness(coordinatorAddress)) return false;

            // Check that the trader is not also the coordinator
            if (traderAddress == coordinatorAddress) return false;

            // Check that the trader is not also the withdrawCoordinator
            if (traderAddress == GetWithdrawCoordinatorAddress()) return false;

            return true;
        }

        private static void TransferNEP5(byte[] from, byte[] to, byte[] assetID, BigInteger amount)
        {
            if (IsPythonNEP5(assetID))
            {
                TransferPythonNEP5(from, to, assetID, amount);
                return;
            }

            // Transfer token
            var args = new object[] { from, to, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            if (!(bool)contract("transfer", args)) throw new Exception("Failed to transfer NEP-5 tokens!");
        }

        private static void TransferPythonNEP5(byte[] from, byte[] to, byte[] assetID, BigInteger amount)
        {
            // Transfer token (with compiler changes)
            var args = new object[] { from, to, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            if (!(bool)contract("transfer", args)) throw new Exception("Failed to transfer NEP-5 tokens!");
        }

        private static void TransferFromNEP5(byte[] from, byte[] to, byte[] assetID, BigInteger amount)
        {
            // Transfer token (using pre-approval)
            var args = new object[] { ExecutionEngine.ExecutingScriptHash, from, to, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            if (!(bool)contract("transferFrom", args)) throw new Exception("Failed to transfer NEP-5 tokens!");
        }

        private static byte[] GetWhitelistKey(byte[] assetID, int whitelistEnum)
        {
            if (whitelistEnum == 0) return OldWhitelistKey(assetID);
            if (whitelistEnum == 1) return NewWhitelistKey(assetID);
            if (whitelistEnum == 2) return PytWhitelistKey(assetID);
            throw new ArgumentOutOfRangeException();
        }

        private static byte[] GetWhitelistSealedKey(int whitelistEnum)
        {
            if (whitelistEnum == 0) return "oldWhitelistSealed".AsByteArray();
            if (whitelistEnum == 1) return "newWhitelistSealed".AsByteArray();
            if (whitelistEnum == 2) return "pytWhitelistSealed".AsByteArray();
            throw new ArgumentOutOfRangeException();
        }

        // Unique hash for an offer
        private static byte[] Hash(Offer o)
        {
            var bytes = o.MakerAddress
                .Concat(o.OfferAssetID)
                .Concat(o.WantAssetID)
                .Concat(o.OfferAmount.AsByteArray())
                .Concat(o.WantAmount.AsByteArray())
                .Concat(o.Nonce);

            return Hash256(bytes);
        }

        // Keys
        private static byte[] OfferKey(byte[] offerHash) => "offers".AsByteArray().Concat(offerHash);
        private static byte[] BalanceKey(byte[] originator, byte[] assetID) => "balance".AsByteArray().Concat(originator).Concat(assetID);
        private static byte[] WithdrawalAddressForTransactionKey(byte[] transactionHash) => "withdrawalUTXO".AsByteArray().Concat(transactionHash);
        private static byte[] WithdrawingKey(byte[] originator, byte[] assetID) => "withdrawing".AsByteArray().Concat(originator).Concat(assetID);
        private static byte[] OldWhitelistKey(byte[] assetID) => "oldNEP5Whitelist".AsByteArray().Concat(assetID);
        private static byte[] NewWhitelistKey(byte[] assetID) => "newNEP5Whitelist".AsByteArray().Concat(assetID);
        private static byte[] PytWhitelistKey(byte[] assetID) => "pytNEP5Whitelist".AsByteArray().Concat(assetID);
        private static byte[] DepositKey(Transaction txn) => "deposited".AsByteArray().Concat(txn.Hash);
        private static byte[] CancelAnnounceKey(byte[] offerHash) => "cancelAnnounce".AsByteArray().Concat(offerHash);
        private static byte[] WithdrawAnnounceKey(byte[] originator, byte[] assetID) => "withdrawAnnounce".AsByteArray().Concat(originator).Concat(assetID);
    }
}

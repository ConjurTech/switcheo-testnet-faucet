﻿using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;

namespace Neo.SmartContract
{
    public class Faucet_Contract : Framework.SmartContract
    {
        public delegate object NEP5Contract(string method, object[] args);

        // Events
        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> Withdrawn; // (address, assetID, amount)

        [DisplayName("withdrawing")]
        public static event Action<byte[], byte[], BigInteger> Withdrawing; // (address, assetID, amount)

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // faucet halted - can only do owner actions

        // Flags / Byte Constants
        private static readonly byte[] Individual = { 0x10 };
        private static readonly byte[] Global = { 0x11 };
        private static readonly byte[] LastWithdrawn = { 0x12 };
        private static readonly byte[] TotalWithdrawn = { 0x13 };        
        private static readonly byte[] Marking = { 0x50 };
        private static readonly byte[] Withdraw = { 0x51 }; // SystemAsset
        private static readonly byte[] Transfer = { 0x52 }; // NEP-5

        // Other Settings
        private static readonly byte[] Owner = "AHDfSLZANnJ4N9Rj3FCokP14jceu3u7Bvw".ToScriptHash();
        private static readonly byte[] gasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        private const ulong faucetInteveral = 3600; // 1 hour

        public static Object Main(string operation, params object[] args)
        {
            // Prepare vars
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var withdrawingAddr = GetWithdrawalAddress(currentTxn);
            var assetID = GetWithdrawalAsset(currentTxn);
            var isWithdrawingNEP5 = assetID.Length == 20;
            var inputs = currentTxn.GetInputs();
            var outputs = currentTxn.GetOutputs();

            if (Runtime.Trigger == TriggerType.Verification)
            {
                // Check that the contract is active
                if (GetState() != Active) return false;

                ulong totalOut = 0;
                if (WithdrawalType(currentTxn) == Marking)
                {
                    // Check that txn is signed
                    if (!Runtime.CheckWitness(withdrawingAddr)) return false;

                    // Check that withdrawal is possible
                    if (!VerifyWithdrawal(withdrawingAddr, assetID)) return false;

                    // Check that inputs are not already reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex))).Length > 0) return false;
                    }

                    // Check that outputs are a valid self-send
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                        if (isWithdrawingNEP5)
                        {
                            if (o.AssetId != gasAssetID) return false;
                        }
                        else
                        {
                            if (o.AssetId != assetID) return false;
                        }
                    }
                    // TODO: should also check outputs.Length for SystemAsset (at most +1 of required)

                    // Check that NEP5 withdrawals don't reserve more utxos than required
                    if (isWithdrawingNEP5)
                    {
                        if (inputs.Length > 1) return false;
                        if (outputs.Length > 2) return false;
                        if (outputs[0].Value > 1) return false;
                    }
                }
                else if (WithdrawalType(currentTxn) == Withdraw)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex))) != withdrawingAddr) return false;
                    }

                    // Check withdrawal amount and destinations
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.AssetId != assetID) return false;
                        if (o.ScriptHash != withdrawingAddr) return false;
                    }
                    if (totalOut != IndividualCap(assetID)) return false;
                }
                else if (WithdrawalType(currentTxn) == Transfer)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex))) != withdrawingAddr) return false;
                    }

                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                    }
                }
                else
                {
                    // Only allow owner to withdraw otherwise
                    return Runtime.CheckWitness(Owner);
                }

                // Check that there is nothing burnt
                ulong totalIn = 0;
                foreach (var i in currentTxn.GetReferences()) totalIn += (ulong)i.Value;
                if (totalIn != totalOut) return false;

                // TODO: Check that Application trigger will be tail called

                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                if (WithdrawalType(currentTxn) == Marking)
                {
                    var amount = IndividualCap(assetID);
                    if (isWithdrawingNEP5)
                    {
                        Runtime.Log("Marking NEP5");
                        MarkWithdrawal(withdrawingAddr, assetID, amount);
                        Storage.Put(Context(), currentTxn.Hash.Concat(new byte[1] { 0 }), withdrawingAddr);
                    }
                    else
                    {
                        Runtime.Log("Marking SystemAsset");
                        MarkWithdrawal(withdrawingAddr, assetID, amount);
                        ulong sum = 0;
                        for (int index = 0; index < outputs.Length; index++)
                        {
                            sum += (ulong)outputs[index].Value;
                            Runtime.Log("output check..");
                            if (sum <= amount)
                            {
                                Runtime.Log("Reserving...");
                                Storage.Put(Context(), currentTxn.Hash.Concat(((BigInteger)index).AsByteArray()), withdrawingAddr);
                            }
                        }
                    }
                    Withdrawing(withdrawingAddr, assetID, amount);
                    return true;
                }
                else if (WithdrawalType(currentTxn) == Withdraw)
                {
                    foreach (var i in inputs)
                    {
                        //Runtime.Notify("del", i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex)));
                        Storage.Delete(Context(), i.PrevHash.Concat(IndexAsByteArray(i.PrevIndex)));
                    }
                    var amount = IndividualCap(assetID);
                    Withdrawn(withdrawingAddr, assetID, IndividualCap(assetID));
                    return true;
                }
                else if (WithdrawalType(currentTxn) == Transfer)
                {
                    var amount = IndividualCap(assetID);
                    WithdrawNEP5(withdrawingAddr, assetID, amount);
                    Withdrawn(withdrawingAddr, assetID, amount);
                    return true;
                }

                if (operation == "initialize")
                {
                    return Initialize();
                }
                if (GetState() == Pending)
                {
                    Runtime.Log("Contract not initialized!");
                    return false;
                }
                if (operation == "getIndividualCap")
                {
                    return IndividualCap((byte[])args[0]);
                }
                if (operation == "getLastWithdrawTime")
                {
                    return Storage.Get(Context(), LastWithdrawnKey((byte[])args[0], (byte[])args[1])).AsBigInteger();
                }

                // == Owner ==
                if (!Runtime.CheckWitness(Owner))
                {
                    Runtime.Log("Owner signature verification failed");
                    return false;
                }
                if (operation == "freezeWithdrawals")
                {
                    Storage.Put(Context(), "state", Inactive);
                    return true;
                }
                if (operation == "unfreezeWithdrawals")
                {
                    Storage.Put(Context(), "state", Active);
                    return true;
                }
                if (operation == "setIndividualCap")
                {
                    Storage.Put(Context(), IndividualCapKey((byte[])args[0]), (BigInteger)args[1]);
                    return true;
                }
                if (operation == "setGlobalCap")
                {
                    Storage.Put(Context(), GlobalCapKey((byte[])args[0]), (BigInteger)args[1]);
                    return true;
                }
            }
            return false;
        }

        private static bool Initialize()
        {
            if (!Runtime.CheckWitness(Owner))
            {
                Runtime.Log("Owner signature verification failed!");
                return false;
            }

            if (GetState() != Pending) return false;

            Storage.Put(Context(), "state", Active);
            Storage.Put(Context(), "startTime", Runtime.Time);

            Runtime.Log("Contract initialized");
            return true;
        }

        private static bool VerifyWithdrawal(byte[] address, byte[] assetID)
        {
            // Check individual cap
            var lastWithdrawn = Storage.Get(Context(), LastWithdrawnKey(address, assetID)).AsBigInteger();
            if (lastWithdrawn + faucetInteveral > Runtime.Time) return false;

            // Check global cap
            var totalWithdrawn = Storage.Get(Context(), TotalWithdrawnKey(assetID)).AsBigInteger();
            var globalCap = Storage.Get(Context(), GlobalCapKey(assetID)).AsBigInteger();
            var startTime = Storage.Get(Context(), "startTime").AsBigInteger();
            var intervalsSinceStart = (Runtime.Time - startTime) / faucetInteveral + 1;
            if (totalWithdrawn > globalCap * intervalsSinceStart) return false;

            return true;
        }

        private static bool MarkWithdrawal(byte[] address, byte[] assetID, BigInteger amount)
        {
            Runtime.Log("Checking Last Mark..");
            if (!VerifyWithdrawal(address, assetID)) return false;

            Runtime.Log("Marking Withdrawal..");
            var totalWithdrawnKey = TotalWithdrawnKey(assetID);
            var totalWithdrawn = Storage.Get(Context(), totalWithdrawnKey).AsBigInteger();
            Storage.Put(Context(), totalWithdrawnKey, totalWithdrawn + amount);
            Storage.Put(Context(), LastWithdrawnKey(address, assetID), Runtime.Time);

            return true;
        }

        private static bool WithdrawNEP5(byte[] address, byte[] assetID, BigInteger amount)
        {
            // Transfer token
            var args = new object[] { ExecutionEngine.ExecutingScriptHash, address, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            bool transferSuccessful = (bool)contract("transfer", args);
            if (!transferSuccessful)
            {
                Runtime.Log("Failed to transfer NEP-5 tokens!");
                return false;
            }

            return true;
        }

        private static byte[] GetWithdrawalAddress(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                // This is the additional verification script which can be used
                // to ensure any withdrawal txns are intended by the owner.
                if (attr.Usage == 0x20) return attr.Data.Take(20);
            }
            return new byte[0] { };
        }

        private static byte[] GetWithdrawalAsset(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa2) return attr.Data.Take(20);
                if (attr.Usage == 0xa3) return attr.Data;
            }
            return new byte[0] { };
        }

        private static byte[] WithdrawalType(Transaction transaction)
        {
            // Check that the transaction is marked as a SystemAsset withdrawal
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa1) return attr.Data.Take(1);
            }

            return new byte[0] { };
        }

        // Helpers
        private static StorageContext Context() => Storage.CurrentContext;
        private static byte[] GetState() => Storage.Get(Context(), "state");
        private static BigInteger IndividualCap(byte[] assetID) => Storage.Get(Context(), IndividualCapKey(assetID)).AsBigInteger();
        private static byte[] IndexAsByteArray(ushort index) => index > 0 ? ((BigInteger)index).AsByteArray() : new byte[1] { 0 };

        // Keys
        private static byte[] GlobalCapKey(byte[] assetID) => Global.Concat(assetID);
        private static byte[] IndividualCapKey(byte[] assetID) => Individual.Concat(assetID);
        private static byte[] LastWithdrawnKey(byte[] originator, byte[] assetID) => LastWithdrawn.Concat(assetID).Concat(originator);
        private static byte[] TotalWithdrawnKey(byte[] assetID) => TotalWithdrawn.Concat(assetID);
    }
}
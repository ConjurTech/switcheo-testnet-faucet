using Neo.SmartContract.Framework;
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
        private static readonly byte[] Withdrawing = { 0x51 }; // SystemAsset
        private static readonly byte[] Transferring = { 0x52 }; // NEP-5

        // Other Settings
        public static readonly byte[] Owner = "AHDfSLZANnJ4N9Rj3FCokP14jceu3u7Bvw".ToScriptHash();
        private const ulong faucetInteveral = 3600; // 1 hour
        private const ulong assetFactor = 100000000; // for neo and gas

        private static StorageContext Context() => Storage.CurrentContext;
        private static byte[] GetState() => Storage.Get(Context(), "state");
        private static BigInteger FaucetInterval() => Storage.Get(Context(), "faucetInterval").AsBigInteger();
        private static BigInteger IndividualCap(byte[] assetID) => Storage.Get(Context(), IndividualCapKey(assetID)).AsBigInteger();

        public static Object Main(string operation, params object[] args)
        {
            // Prepare vars
            var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
            var withdrawingAddr = GetWithdrawalAddress(currentTxn);
            var NEP5AssetID = GetWithdrawalNEP5(currentTxn);
            var isWithdrawingNEP5 = NEP5AssetID.Length == 20;
            var inputs = currentTxn.GetInputs();
            var outputs = currentTxn.GetOutputs();

            if (Runtime.Trigger == TriggerType.Verification)
            {
                // Check that is either owner, or user will invoke application later
                if (Runtime.CheckWitness(Owner)) return true;

                // Check that the contract is initialized
                if (GetState() == Pending) return false;

                ulong totalOut = 0;
                if (WithdrawalType(currentTxn) == Marking)
                {
                    // Check that txn is signed
                    if (!Runtime.CheckWitness(withdrawingAddr)) return false;

                    // Check that inputs are not already reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(((BigInteger)i.PrevIndex).AsByteArray())).Length > 0) return false;
                    }

                    // Check that this is not a DOS and outputs are a valid self-send
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (!isWithdrawingNEP5 && !VerifyWithdrawal(withdrawingAddr, o.AssetId)) return false;
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;
                    }
                    if (isWithdrawingNEP5 && outputs.Length > 1) return false;

                    // Check that there are no splits
                    if (inputs.Length != outputs.Length) return false;
                }
                else if (WithdrawalType(currentTxn) == Withdrawing)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(((BigInteger)i.PrevIndex).AsByteArray())) != withdrawingAddr) return false;
                    }

                    // Check withdrawal amount
                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                        {
                            // Only can withdraw to the marked addr
                            if (o.ScriptHash != withdrawingAddr) return false;

                            // TODO: optimize this using Map
                            var amountWithdrawn = GetAmountForAssetInOutputs(o.AssetId, outputs);
                            if (amountWithdrawn > IndividualCap(o.AssetId)) return false;
                        }
                    }
                }
                else if (WithdrawalType(currentTxn) == Transferring)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        if (Storage.Get(Context(), i.PrevHash.Concat(((BigInteger)i.PrevIndex).AsByteArray())) != withdrawingAddr) return false;
                    }

                    foreach (var o in outputs)
                    {
                        totalOut += (ulong)o.Value;
                    }
                }
                else
                {
                    return false;
                }

                // Check that there is nothing burnt
                ulong totalIn = 0;
                foreach (var i in currentTxn.GetReferences()) totalIn += (ulong)i.Value;
                if (totalIn != totalOut) return false;

                // TODO: Check that Application trigger will be called

                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                if (WithdrawalType(currentTxn) == Marking)
                {                    
                    if (isWithdrawingNEP5)
                    {
                        MarkWithdrawal(withdrawingAddr, NEP5AssetID);
                        Storage.Put(Context(), currentTxn.Hash.Concat(new byte[1] { 0 }), withdrawingAddr);
                    }
                    else
                    {
                        // TODO: use Map when avaiable in neo-compiler
                        // var assets = new Dictionary<byte[], BigInteger>();
                        BigInteger index = 0;
                        foreach (var o in outputs)
                        {
                            // assets.TryGetValue(o.AssetId, out BigInteger sum);
                            var sum = 0;
                            MarkWithdrawal(withdrawingAddr, o.AssetId);
                            if (sum + o.Value <= IndividualCap(o.AssetId))
                            {
                                Storage.Put(Context(), currentTxn.Hash.Concat(index.AsByteArray()), withdrawingAddr);
                            }
                            index += 1;
                            // assets.Add(o.AssetId, sum + o.Value);
                        }
                    }
                }
                else if (WithdrawalType(currentTxn) == Withdrawing)
                {
                    // Check that utxo has been reserved
                    foreach (var i in inputs)
                    {
                        Storage.Delete(Context(), i.PrevHash.Concat(((BigInteger)i.PrevIndex).AsByteArray()));
                    }
                    foreach (var o in outputs)
                    {
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) Withdrawn(o.ScriptHash, o.AssetId, o.Value);
                    }
                }
                else if (WithdrawalType(currentTxn) == Transferring)
                {
                    WithdrawNEP5(withdrawingAddr, GetWithdrawalNEP5(currentTxn));
                }

                if (operation == "initialize")
                {
                    if (!Runtime.CheckWitness(Owner))
                    {
                        Runtime.Log("Owner signature verification failed!");
                        return false;
                    }
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
                    Storage.Put(Context(), IndividualCapKey((byte[])args[0]), (BigInteger)args[1]); // how much max asset to be withdrawn per time interval
                    return true;
                }
            }
            return false;
        }

        private static bool Initialize()
        {
            if (GetState() != Pending) return false;

            Storage.Put(Context(), "state", Active);
            Storage.Put(Context(), "startTime", Runtime.Time);

            Runtime.Log("Contract initialized");
            return true;
        }

        private static bool VerifyWithdrawal(byte[] address, byte[] assetID)
        {
            var interval = FaucetInterval();

            // Check individual cap
            var lastWithdrawn = Storage.Get(Context(), LastWithdrawnKey(address, assetID)).AsBigInteger();
            if (lastWithdrawn != 0 && lastWithdrawn + interval > Runtime.Time) return false;

            // Check global cap
            var totalWithdrawn = Storage.Get(Context(), TotalWithdrawnKey(assetID)).AsBigInteger();
            var globalCap = Storage.Get(Context(), GlobalCapKey(assetID)).AsBigInteger();
            var startTime = Storage.Get(Context(), "startTime").AsBigInteger();
            var intervalsSinceStart = (startTime - Runtime.Time) / interval;
            if (totalWithdrawn > globalCap * intervalsSinceStart) return false;

            return true;
        }

        private static void MarkWithdrawal(byte[] address, byte[] assetID)
        {
            var totalWithdrawnKey = TotalWithdrawnKey(assetID);
            var totalWithdrawn = Storage.Get(Context(), totalWithdrawnKey).AsBigInteger();
            Storage.Put(Context(), totalWithdrawnKey, totalWithdrawn + IndividualCap(assetID));
            Storage.Put(Context(), LastWithdrawnKey(address, assetID), Runtime.Time);
        }

        private static bool WithdrawNEP5(byte[] address, byte[] assetID)
        {
            // Transfer token
            var amount = IndividualCap(assetID);
            var args = new object[] { ExecutionEngine.ExecutingScriptHash, address, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            bool transferSuccessful = (bool)contract("transfer", args);
            if (!transferSuccessful)
            {
                Runtime.Log("Failed to transfer NEP-5 tokens!");
                return false;
            }

            // Notify clients
            Withdrawn(address, assetID, amount);

            return true;
        }

        private static ulong GetAmountForAssetInOutputs(byte[] assetID, TransactionOutput[] outputs)
        {
            ulong amount = 0;
            foreach (var o in outputs)
            {
                if (o.AssetId == assetID && o.ScriptHash != ExecutionEngine.ExecutingScriptHash) amount += (ulong)o.Value;
            }

            return amount;
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

        private static byte[] GetWithdrawalNEP5(Transaction transaction)
        {
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0x21) return attr.Data.Take(20);
            }
            return new byte[0] { };
        }

        private static byte[] WithdrawalType(Transaction transaction)
        {
            // Check that the transaction is marked as a SystemAsset withdrawal
            var txnAttributes = transaction.GetAttributes();
            foreach (var attr in txnAttributes)
            {
                if (attr.Usage == 0xa1) return attr.Data.Take(2);
            }

            return new byte[0] { };
        }

        private static byte[] GlobalCapKey(byte[] assetID) => Global.Concat(assetID);
        private static byte[] IndividualCapKey(byte[] assetID) => Individual.Concat(assetID);
        private static byte[] LastWithdrawnKey(byte[] originator, byte[] assetID) => LastWithdrawn.Concat(assetID).Concat(originator);
        private static byte[] TotalWithdrawnKey(byte[] assetID) => TotalWithdrawn.Concat(assetID);
    }
}
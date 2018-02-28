using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

namespace Neo.SmartContract
{
    public class Token_Faucet : Framework.SmartContract
    {
        public delegate object NEP5Contract(string method, object[] args);

        // Events
        [DisplayName("withdrawn")]
        public static event Action<byte[], byte[], BigInteger> Withdrawn; // (address, assetID, amount)

        // Contract States
        private static readonly byte[] Pending = { };         // only can initialize
        private static readonly byte[] Active = { 0x01 };     // all operations active
        private static readonly byte[] Inactive = { 0x02 };   // faucet halted - can only do owner actions

        // Asset Categories
        private static readonly byte[] SystemAsset = { 0x99 };
        private static readonly byte[] NEP5 = { 0x98 };
        
        // Flags / Byte Constants
        private static readonly byte[] Empty = { };
        private static readonly byte[] Withdrawing = { 0x50 };

        // Other Settings
        private const ulong presaleAmount = 800_000_000 * factor; // private sale amount
        private const ulong saleT1Amount = 150_000_000 * factor; // public sale amount for tier 1

        public static readonly byte[] Owner = "AHDfSLZANnJ4N9Rj3FCokP14jceu3u7Bvw".ToScriptHash();

        private const ulong assetFactor = 100000000; // for neo and gas
        private static readonly byte[] neoAssetID = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] gasAssetID = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };

        public static Object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                // == Withdrawal of SystemAsset ==
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;

                // Check that is either owner, or user will invoke application later
                if (Runtime.CheckWitness(owner)) return true;
                if (!IsInvokingApplicationTrigger(currentTxn)) return false;

                // Verify that the contract is initialized
                if (GetState() == Pending) return false;

                // Get the withdrawing address
                var withdrawingAddr = GetWithdrawalAddress(currentTxn);

                // Verify that each output is allowed
                var outputs = currentTxn.GetOutputs();
                foreach (var o in outputs)
                {
                    // Get amount for each asset
                    var amount = GetAmountForAssetInOutputs(o.AssetId, outputs);
                    if (!VerifyWithdrawal(amount, o.AssetId, withdrawingAddr)) return false;
                }
                return true;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                if (operation == "initialize")
                {
                    if (!Runtime.CheckWitness(Owner))
                    {
                        Runtime.Log("Owner signature verification failed!");
                        return false;
                    }
                    if (args.Length != 3) return false;
                    return Initialize((BigInteger)args[0], (BigInteger)args[1], (byte[])args[2]);
                }
                if (GetState() == Pending)
                {
                    Runtime.Log("Contract not initialized!");
                    return false;
                }
                if (operation == "withdrawAssets")
                {
                    if (args.Length != 3) return false;
                    WithdrawAssets((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                }
                }
                if (operation == "availableAmountForWithdrawal")
                {
                    AvailableAmountForWithdrawal((byte[])args[0]);
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
                if (operation == "setFaucetLimit")
                {
                    Storage.Put(Context(), "faucetLimit", (BigInteger)args[1]); // how much max asset to be withdrawn per time interval
                    return true;
                }
                if (operation == "setFaucetInterval")
                {
                    Storage.Put(Context(), "faucetInterval", (BigInteger)args[1]); // in seconds
                    return true;
                }
            }
            return false;
        }
        
        private static bool Initialize()
        {
            if (GetState() != Pending) return false;

            Storage.Put(Context(), "state", Active);
            Storage.Put(Context(), "faucetInterval", 60 * 60); // 1 hour
            Storage.Put(Context(), "faucetLimit", 1000000); // TODO: how much?

            Runtime.Log("Contract initialized");
            return true;
        }

        private static bool VerifyWithdrawal(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Check that address has enough quota to withdraw
            var availableAmount = AvailableAmountForWithdrawal(holderAddress, assetID);
            if (availableAmount < amount) return false;

            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, BigInteger amount)
        {
            // Transfer token
            var args = new object[] { ExecutionEngine.ExecutingScriptHash, holderAddress, amount };
            var contract = (NEP5Contract)assetID.ToDelegate();
            bool transferSuccessful = (bool)contract("transfer", args);
            if (!transferSuccessful)
            {
                Runtime.Log("Failed to transfer NEP-5 tokens!");
                return false;
            }

            // Add to storage
            if (!AddToTotalWithdrawal(holderAddress, assetID, amount)) return false;

            // Notify clients
            Withdrawn(holderAddress, assetID, amount);

            return true;
        }

        private static bool PrepareAssetWithdrawal(byte[] holderAddress)
        {
            // Check that transaction is signed by the asset holder
            if (!Runtime.CheckWitness(holderAddress)) return false;

            // Get the key which marks start of withdrawal process
            var withdrawalKey = WithdrawalKey(holderAddress);

            // Check if already withdrawing
            if (Storage.Get(Context(), withdrawalKey) != Empty) return false;

            // Set blockheight from which to check for double withdrawals later on
            Storage.Put(Context(), withdrawalKey, Blockchain.GetHeight());

            Runtime.Log("Prepared for asset withdrawal");

            return true;
        }

        private static BigInteger AvailableAmountForWithdrawal(byte[] address, byte[] assetID)
        {
            var totalWithdrawnKey = TotalWithdrawnKey(address, assetID);
            var currentTotalWithdrawn = Storage.Get(Context(), TotalWithdrawnKey).AsBigInteger();
            uint faucetLimit = Storage.Get(Context(), 'faucetLimit').AsBigInteger();
            return faucetLimit - currentTotalWithdrawn;
        }

        private static bool AddToTotalWithdrawal(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount < 1)
            {
                Runtime.Log("Amount to withdraw is less than 1!");
                return false;
            }

            var totalWithdrawnKey = TotalWithdrawnKey(address, assetID);
            var lastWithdrawalTimeKey = LastWithdrawalTimeKey(address, assetID);

            var lastWithdrawalTime = Storage.Get(Context(), lastWithdrawalTimeKey).AsBigInteger();
            uint currentFaucetInterval = Storage.Get(Context(), 'faucetInterval').AsBigInteger();
            uint faucetLimit = Storage.Get(Context(), 'faucetLimit').AsBigInteger();
            
            // Reset Total Withdrawn if time interval passed
            if (Runtime.Time - lastWithdrawalTime > currentFaucetInterval)
            {
                ResetWithdrawal(address, assetID);
            }

            // Calculate new total withdrawn in this interval
            var currentTotalWithdrawn = Storage.Get(Context(), TotalWithdrawnKey).AsBigInteger();
            var newTotal = currentTotalWithdrawn + amount;

            if (newTotal < 0 || newTotal > faucetLimit)
            {
                Runtime.Log("Invalid total!");
                return false;
            }

            // Store new total
            Storage.Put(Context(), totalWithdrawnKey, newTotal);

            return true;
        }

        private static bool ResetWithdrawal(byte[] holderAddress, byte[] assetID)
        {
          var totalWithdrawnKey = TotalWithdrawnKey(address, assetID);
          var lastWithdrawalTimeKey = LastWithdrawalTimeKey(address, assetID);
          Storage.Put(Context(), totalWithdrawnKey, 0);
          Storage.Put(Context(), lastWithdrawalTimeKey, Runtime.Time);
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
            return Empty;
        }

        private static byte[] GetState()
        {
            return Storage.Get(Context(), "state");
        }

        private static byte[] TotalWithdrawnKey(byte[] originator, byte[] assetID) => originator.Concat(assetID);
        private static byte[] LastWithdrawalTimeKey(byte[] originator, byte[] assetID) => originator.Concat(assetID).Concat('time');
        private static byte[] WithdrawalKey(byte[] originator) => originator.Concat(Withdrawing);
    }
}
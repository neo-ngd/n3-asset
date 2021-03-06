using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System;
using System.ComponentModel;
using System.Numerics;

namespace pnUSDT
{
    [ManifestExtra("Author", "Neo")]
    [ManifestExtra("Email", "dev@neo.org")]
    [ManifestExtra("Description", "This is a contract for USDT on Neo")]
    [SupportedStandards("NEP-17")]
    [ContractPermission("*")]
    public class pnUSDT : SmartContract
    {
        #region Notifications

        [DisplayName("Transfer")]
        public static event Action<UInt160, UInt160, BigInteger> OnTransfer;

        [DisplayName("Notify")]
        public static event Action<string, object, object> Notify;

        #endregion

        //initial operator
        [InitialValue("NYxb4fSZVKAz8YsgaPK2WkT3KcAE9b3Vag", ContractParameterType.Hash160)]
        private static readonly UInt160 Owner = default;
        private static readonly byte[] SupplyKey = "sk".ToByteArray();
        private static readonly byte[] BalancePrefix = new byte[] { 0x01, 0x01 };
        private static readonly byte[] ContractPrefix = new byte[] { 0x01, 0x02 };
        private static readonly byte[] OwnerKey = "owner".ToByteArray();

        public static readonly StorageMap BalanceMap = new StorageMap(Storage.CurrentContext, BalancePrefix);
        public static readonly StorageMap ContractMap = new StorageMap(Storage.CurrentContext, ContractPrefix);

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            ContractMap.Put(OwnerKey, Owner);
        }

        private static bool IsOwner() => Runtime.CheckWitness(GetOwner());

        public static UInt160 GetOwner() => (UInt160)ContractMap.Get(OwnerKey);

        // When this contract address is included in the transaction signature,
        // this method will be triggered as a VerificationTrigger to verify that the signature is correct.
        // For example, this method needs to be called when withdrawing token from the contract.
        public static bool Verify() => IsOwner();       

        private static void SupplyPut(BigInteger value) => ContractMap.Put(SupplyKey, value);

        private static void SupplyIncrease(BigInteger value) => SupplyPut(TotalSupply() + value);

        private static void AssetPut(UInt160 key, BigInteger value) => BalanceMap.Put(key, value);

        private static void AssetIncrease(UInt160 key, BigInteger value) => AssetPut(key, BalanceOf(key) + value);

        private static void Remove(UInt160 key) => BalanceMap.Delete(key);

        private static void AssetReduce(UInt160 key, BigInteger value)
        {
            var oldValue = BalanceOf(key);
            if (oldValue == value)
                Remove(key);
            else
                AssetPut(key, oldValue - value);
        }

        #region Nep-17 Methods

        public static string Symbol() => "pnUSDT";

        public static byte Decimals() => 6;

        public static BigInteger BalanceOf(UInt160 address) => (BigInteger)BalanceMap.Get(address);

        public static BigInteger TotalSupply() => (BigInteger)ContractMap.Get(SupplyKey);

        public static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, object data = null)
        {
            Assert(from.Length == 20, "The from address is invalid.");
            Assert(to.Length == 20, "The to address is invalid.");
            Assert(amount > 0, "The parameter amount must be greater than 0.");
            Assert(Runtime.CheckWitness(from) || from.Equals(Runtime.CallingScriptHash), "No authorization.");
            Assert(BalanceOf(from) >= amount, "Insufficient balance.");

            if (from == to) return true;

            AssetReduce(from, amount);
            AssetIncrease(to, amount);

            OnTransfer(from, to, amount);

            // Validate payable
            if (ContractManagement.GetContract(to) != null)
                Contract.Call(to, "onNEP17Payment", CallFlags.All, from, amount, data);
            return true;
        }

        #endregion

        public static void Init(UInt160 proxyHash, BigInteger supply)
        {
            Assert(proxyHash.IsValid, "The proxyHash address is invalid.");
            Assert(IsOwner(), "No authorization.");
            Assert((BigInteger)ContractMap.Get(SupplyKey) == 0, "InitSupply can only be set up one time");

            SupplyPut(supply);
            AssetPut(proxyHash, supply);

            OnTransfer(null, proxyHash, supply);
        }

        public static void Mint(UInt160 proxyHash, BigInteger increase)
        {
            Assert(proxyHash.IsValid, "The proxyHash address is invalid.");
            Assert((BigInteger)ContractMap.Get(SupplyKey) > 0, "Need init first");
            Assert(IsOwner(), "No authorization.");

            SupplyIncrease(increase);
            AssetIncrease(proxyHash, increase);

            OnTransfer(null, proxyHash, increase);
        }        

        public static bool TransferOwnership(UInt160 newOwner)
        {
            // transfer contract ownership from current owner to a new owner
            Assert(newOwner.IsValid, "The new owner address is invalid.");
            Assert(IsOwner(), "No authorization.");

            ContractMap.Put(OwnerKey, newOwner);
            return true;
        }

        public static void Update(ByteString nefFile, string manifest)
        {
            Assert(IsOwner(), "No authorization.");

            ContractManagement.Update(nefFile, manifest);
        }

        public static void Destroy()
        {
            Assert(IsOwner(), "No authorization.");

            ContractManagement.Destroy();
        }

        private static void Assert(bool condition, string msg, object result = null, string errorType = "Error")
        {
            if (!condition)
            {
                Notify(errorType, result, msg);
                throw new InvalidOperationException(msg);
            }
        }
    }
}

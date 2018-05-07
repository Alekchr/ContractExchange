using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;


namespace ExchangeContract
{
    public class Contract : SmartContract
    {

        [DisplayName("created")]
        public static event Action<byte[], byte[], byte[], BigInteger, byte[], BigInteger> Created; 

        [DisplayName("accepted")]
        public static event Action<byte[], byte[], BigInteger, byte[], BigInteger, byte[], BigInteger> Accepted;

        private static readonly byte[] Empty = { };
        public delegate object NEP5Contract(string method, object[] args);

        private static readonly byte[] Inactive = { 0x02 };

        private struct Order
        {
            public byte[] CreatorAddress;
            public byte[] OfferTokenID;
            public byte[] WantTokenID;
            public BigInteger OfferAmount;
            public BigInteger WantAmount;
            public BigInteger AvailableAmount;
            public byte[] Nonce;
        }


        private static Order NewOrder(byte[] creatorAddress, byte[] offerTokenID, byte[] offerAmount,
            byte[] wantAssetID, byte[] wantAmount, byte[] availableAmount, byte[] nonce
        )
        {
            Order order = new Order();
            
                order.CreatorAddress = creatorAddress;
                order.OfferTokenID = offerTokenID;
                order.OfferAmount = offerAmount.AsBigInteger();
                order.WantTokenID = wantAssetID;
                order.WantAmount = wantAmount.AsBigInteger();
                order.AvailableAmount = availableAmount.AsBigInteger();
                order.Nonce = nonce;
            return order;
        }




        public static bool Main(string operation, params object[] args)
        {
            switch (operation)
            {
                case "deposit":
                    return Deposit((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                case "create":
                    return CreateOrder(
                     NewOrder((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[5], (byte[])args[6]));
                case "accept":
                    return AcceptOrder((byte[])args[0], (byte[])args[1], (byte[])args[2], (BigInteger)args[3]);
                case "remove":
                    return RemoveOrder((byte[])args[0], (byte[])args[1]);

            }
            return false;
        }

        private static bool Deposit(byte[] userAddress, byte[] depositAssetID, BigInteger depositAmount)
        {
            if (!VerifySentAmount(userAddress, depositAssetID, depositAmount)) return false;
            TransferToken(userAddress, depositAssetID, depositAmount);
            return true;
        }

        private static bool CreateOrder(Order order)
        {
            if (!Runtime.CheckWitness(order.CreatorAddress)) return false;

            // Update balance
            if (!(order.OfferAmount > 0 && order.WantAmount > 0)) return false;
            if (!UpdateBalance(order.CreatorAddress, order.OfferTokenID, order.OfferAmount)) return false;

            // Add the order to storage
            var tradingPair = TradingPair(order);
            var orderHash = Hash(order);
            if (Storage.Get(Storage.CurrentContext, tradingPair.Concat(orderHash)) != Empty) return false;
            StoreOrder(tradingPair, orderHash, order);

            Created(order.CreatorAddress, orderHash, order.OfferTokenID, order.OfferAmount, order.WantTokenID, order.WantAmount);
            return true;
        }

        private static bool RemoveOrder(byte[] tradingPair, byte[] orderHash)
        {
            Order order = GetOrder(tradingPair, orderHash);

            if (order.CreatorAddress == Empty) return false;

            // Check that transaction is signed.
            if (!Runtime.CheckWitness(order.CreatorAddress)) return false;

            TransferToken(order.CreatorAddress, order.OfferTokenID, order.AvailableAmount);

            // Remove order
            Storage.Delete(Storage.CurrentContext, tradingPair.Concat(orderHash));

            return true;
        }



        private static bool AcceptOrder(byte[] fillerAddress, byte[] tradingPair, byte[] orderHash, BigInteger amountToFill)
        {
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            Order order = GetOrder(tradingPair, orderHash);
            if (fillerAddress == order.CreatorAddress) return false;
            if (amountToFill > 0) return false;

            // Calculate amount that can be filled
            BigInteger amountToTake = (order.OfferAmount * amountToFill) / order.WantAmount;

            order.AvailableAmount = order.AvailableAmount - amountToTake;

            // Reduce available balance for the filled token and amount
            if(!UpdateBalance(fillerAddress, order.WantTokenID, amountToFill)) return false;

            // Move token to the taker and creator
            TransferToken(fillerAddress, order.OfferTokenID, amountToTake);
            TransferToken(order.CreatorAddress, order.WantTokenID, amountToFill);

            StoreOrder(tradingPair, orderHash, order);

            Accepted(fillerAddress, orderHash, amountToFill, order.OfferTokenID, order.OfferAmount, order.WantTokenID, order.WantAmount);
            return true;
        }


        private static void StoreOrder(byte[] tradingPair, byte[] orderHash, Order order)
        {
            // Remove order if completely filled
            if (order.AvailableAmount == 0)
            {
                Storage.Delete(Storage.CurrentContext, tradingPair.Concat(orderHash));
            }
            // Otherwise, store the order   
            else
            {
                Runtime.Log("Serializing order");
                var orderData = order.Serialize();
                Storage.Put(Storage.CurrentContext, tradingPair.Concat(orderHash), orderData);
            }
        }

        private static void TransferToken(byte[] originator, byte[] assetID, BigInteger amount)
        {
            byte[] key = BalanceKey(originator, assetID);
            BigInteger currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static bool UpdateBalance(byte[] address, byte[] assetID, BigInteger amount)
        {
            if (amount <= 0) return false;

            var key = BalanceKey(address, assetID);
            var currentBalance = Storage.Get(Storage.CurrentContext, key).AsBigInteger();
            var newBalance = currentBalance - amount;

            if (newBalance < 0)
            {
                Runtime.Log("Balance too low");
                return false;
            }

            if (newBalance > 0)
                Storage.Put(Storage.CurrentContext, key, newBalance);
            else
                Storage.Delete(Storage.CurrentContext, key);

            return true;
        }

        private static bool VerifySentAmount(byte[] originator, byte[] assetID, BigInteger amount)
        {
            // Verify that the offer really has the indicated assets available
            if (assetID.Length == 32)
            {
                // Check the current transaction for the system assets
                var currentTxn = (Transaction)ExecutionEngine.ScriptContainer;
                var outputs = currentTxn.GetOutputs();
                ulong sentAmount = 0;
                foreach (var o in outputs)
                {
                    if (o.AssetId == assetID && o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                    {
                        sentAmount += (ulong)o.Value;
                    }
                }

                // Check that the sent amount is correct
                if (sentAmount != amount)
                {
                    return false;
                }

                // Check that there is no double deposit
                var alreadyVerified = Storage.Get(Context(), currentTxn.Hash.Concat(assetID)).Length > 0;
                if (alreadyVerified) return false;

               
                Storage.Put(Context(), currentTxn.Hash.Concat(assetID), 1);

                return true;
            }
            else if (assetID.Length == 20)
            {
                if (!VerifyContract(assetID)) return false;
                var args = new object[] { originator, ExecutionEngine.ExecutingScriptHash, amount };
                var Contract = (NEP5Contract)assetID.ToDelegate();
                var transferSuccessful = (bool)Contract("transfer", args);
                return transferSuccessful;
            }
            return false;
        }

        private static bool VerifyContract(byte[] assetID)
        {
            if (Storage.Get(Context(), "stateContractWhitelist") == Inactive) return true;
            return Storage.Get(Context(), WhitelistKey(assetID)).Length > 0;
        }

        private static Order GetOrder(byte[] tradingPair, byte[] hash)
        {
            byte[] orderData = Storage.Get(Storage.CurrentContext, tradingPair.Concat(hash));
            if (orderData.Length == 0) return new Order();

            return (Order)orderData.Deserialize();
        }

        private static byte[] Hash(Order o)
        {
            var bytes = o.CreatorAddress
                .Concat(TradingPair(o))
                .Concat(o.OfferAmount.AsByteArray())
                .Concat(o.WantAmount.AsByteArray())
                .Concat(o.Nonce);

            return Hash256(bytes);
        }


        private static StorageContext Context() => Storage.CurrentContext;
        private static byte[] TradingPair(Order o) => o.OfferTokenID.Concat(o.WantTokenID);
        private static byte[] BalanceKey(byte[] trader, byte[] assetID) => trader.Concat(assetID);
        private static byte[] WhitelistKey(byte[] assetID) => "contractWhitelist".AsByteArray().Concat(assetID);





    }



}

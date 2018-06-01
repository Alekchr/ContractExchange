using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using System.Numerics;


namespace ExchangeContract
{
    public class Contract : SmartContract
    {

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


        /*    Parameter types: 0710
         *    Return type: 05
         * 
         **/

        public static object Main(string operation, params object[] args)
        {
            switch (operation)
            {
                case "deposit":
                    if (args.Length != 3) return false;
                    return Deposit((byte[])args[0], (byte[])args[1], (BigInteger)args[2]);
                case "create":
                    if (args.Length != 7) return false;
                    return CreateOrder(
                    NewOrder((byte[])args[0], (byte[])args[1], (byte[])args[2], (byte[])args[3], (byte[])args[4], (byte[])args[5], (byte[])args[6]));
                case "accept":
                    if (args.Length != 4) return false;
                    return AcceptOrder((byte[])args[0], (byte[])args[1], (byte[])args[2], (BigInteger)args[3]);
                case "remove":
                    if (args.Length != 2) return false;
                    return RemoveOrder((byte[])args[0], (byte[])args[1]);
                

            }
            return false;
        }

        private static bool Deposit(byte[] userAddress, byte[] depositAssetID, BigInteger depositAmount)
        {
            byte[] key = BalanceKey(userAddress, depositAssetID);
            BigInteger currentBalance = Storage.Get(Context(), key).AsBigInteger();
            Storage.Put(Context(), key, currentBalance + depositAmount);
            return true;
        }

        private static bool CreateOrder(Order order)
        {
            if (!Runtime.CheckWitness(order.CreatorAddress)) return false;

            // Update balance
            if (!UpdateBalance(order.CreatorAddress, order.OfferTokenID, order.OfferAmount)) return false;

            // Add the order to storage
            var tradingPair = TradingPair(order);
            var orderHash = Hash(order);
            if (Storage.Get(Storage.CurrentContext, tradingPair.Concat(orderHash)) != Empty) return false;
            StoreOrder(tradingPair, orderHash, order);
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
            if (!UpdateBalance(fillerAddress, order.WantTokenID, amountToFill)) return false;

            // Move token to the taker and creator
            TransferToken(fillerAddress, order.OfferTokenID, amountToTake);
            TransferToken(order.CreatorAddress, order.WantTokenID, amountToFill);

            StoreOrder(tradingPair, orderHash, order);
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
                Runtime.Log("Storing order");
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

    }



}

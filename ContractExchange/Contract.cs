using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;


namespace ExchangeContract
{
    public class TheContract : SmartContract
    {

        private static readonly byte[] Empty = { };

        private struct Order
        {
            public byte[] CreatorAddress;
            public byte[] OfferTokenID;
            public byte[] WantTokenID;
            public int OfferAmount;
            public int WantAmount;
            public int AvailableAmount;
            public byte[] Nonce;
        }


        private static Order NewOrder(byte[] creatorAddress, byte[] offerTokenID, byte[] offerAmount,
            byte[] wantAssetID, byte[] wantAmount, byte[] availableAmount, byte[] nonce
        )
        {
            return new Order
            {
                CreatorAddress = creatorAddress.Take(20),
                OfferTokenID = offerTokenID,
                OfferAmount = BitConverter.ToInt32(offerAmount, 0),
                WantTokenID = wantAssetID,
                WantAmount = BitConverter.ToInt32(wantAmount, 0),
                AvailableAmount = BitConverter.ToInt32(availableAmount, 0),
                Nonce = nonce
            };
        }





        public static void Main(string operation, params object[] args)
        {

        }


        private static bool CreateOrder(Order order)
        {
            // Check if transaction is signed by creator
            if (!Runtime.CheckWitness(order.CreatorAddress)) return false;

            // Update balance
            if (!(order.OfferAmount > 0 && order.WantAmount > 0)) return false;
            if (!UpdateBalance(order.CreatorAddress, order.OfferTokenID, order.OfferAmount)) return false;

            // Add the order to storage
            var tradingPair = TradingPair(order);
            var orderHash = Hash(order);
            if (Storage.Get(Storage.CurrentContext, tradingPair.Concat(orderHash)) != Empty) return false;
            StoreOrder(tradingPair, orderHash, order);

            return true;
        }

        private static bool AcceptOrder(byte[] fillerAddress, byte[] tradingPair, byte[] orderHash, int amountToFill, bool useNativeTokens)
        {
            // Check that transaction is signed
            if (!Runtime.CheckWitness(fillerAddress)) return false;

            // Check that the filler is different from the creator
            Order order = GetOrder(tradingPair, orderHash);
            if (fillerAddress == order.CreatorAddress) return false;

            // Calculate amount that can be offered & filled
            int amountToTake = (order.OfferAmount * amountToFill) / order.WantAmount;

            // Reduce available balance for the filled token and amount
            if (amountToFill > 0 && !UpdateBalance(fillerAddress, order.WantTokenID, amountToFill)) return false;

            // Move token to the taker and creator
            TransferToken(fillerAddress, order.OfferTokenID, amountToTake);
            TransferToken(order.CreatorAddress, order.WantTokenID, amountToFill);

            // Update available amount
            order.AvailableAmount = order.AvailableAmount - amountToTake;

            // Store updated order
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
            // Store order otherwise
            else
            {
                Runtime.Log("Serializing order");
                var orderData = order.Serialize();
                Storage.Put(Storage.CurrentContext, tradingPair.Concat(orderHash), orderData);
            }
        }

        private static void TransferToken(byte[] originator, byte[] assetID, int amount)
        {
            byte[] key = BalanceKey(originator, assetID);
            int currentBalance = BitConverter.ToInt32(Storage.Get(Storage.CurrentContext, key), 0);
            Storage.Put(Storage.CurrentContext, key, currentBalance + amount);
        }

        private static bool UpdateBalance(byte[] address, byte[] assetID, int amount)
        {
            if (amount <= 0) return false;

            var key = BalanceKey(address, assetID);
            var currentBalance = BitConverter.ToInt32(Storage.Get(Storage.CurrentContext, key), 0);
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

            Runtime.Log("Deserializing order");
            return (Order)orderData.Deserialize();
        }

        private static byte[] Hash(Order o)
        {
            var bytes = o.CreatorAddress
                .Concat(TradingPair(o))
                .Concat(BitConverter.GetBytes(o.OfferAmount))
                .Concat(BitConverter.GetBytes(o.WantAmount))
                .Concat(o.Nonce);

            return Hash256(bytes);
        }


        private static StorageContext Context() => Storage.CurrentContext;
        private static byte[] TradingPair(Order o) => o.OfferTokenID.Concat(o.WantTokenID);
        private static byte[] BalanceKey(byte[] originator, byte[] assetID) => originator.Concat(assetID);


    }



}

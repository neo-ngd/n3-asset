using Neo;
using Neo.Cryptography.ECC;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System;
using System.ComponentModel;
using System.Numerics;

namespace CrossChain_Contract
{
    [ContractPermission("*")]
    public class CCMC : SmartContract
    {
        //TODO: fill in address
        [InitialValue("NYxb4fSZVKAz8YsgaPK2WkT3KcAE9b3Vag", ContractParameterType.Hash160)]
        private static readonly UInt160 originOwner = default;

        //To be compatible with (ByteString)merkleValue.TxParam.toChainID（11）
        private static readonly byte[] chainID = new byte[] { 0x0b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        //Reuqest prefix
        private static readonly byte[] requestIDPrefix = new byte[] { 0x01, 0x01 };
        private static readonly byte[] requestPreifx = new byte[] { 0x01, 0x02 };

        //Header prefix
        private static readonly byte[] currentEpochHeightPrefix = new byte[] { 0x02, 0x01 };
        private static readonly byte[] mCKeeperPubKeysPrefix = new byte[] { 0x02, 0x04 };

        //tx prefix
        private static readonly byte[] transactionPrefix = new byte[] { 0x03, 0x01 };

        //owner prefix        
        private static readonly byte[] ownerPrefix = new byte[] { 0x04, 0x01 };

        private static readonly byte[] isGenesisedKey = new byte[] { 0x05, 0x01 };
        //constant
        private static readonly int MCCHAIN_PUBKEY_LEN = 67;
        private static readonly int MCCHAIN_SIGNATURE_LEN = 65;
        private static readonly int MCCHAIN_SIGNATUREWITHOUTV_LEN = 64;

        //------------------------------event--------------------------------
        //CrossChainLockEvent "from address" "from contract" "to chain id" "key" "tx param"
        public static event Action<byte[], byte[], BigInteger, byte[], byte[]> CrossChainLockEvent;
        //CrossChainUnlockEvent fromChainID, TxParam.toContract, txHash
        public static event Action<byte[], byte[], byte[]> CrossChainUnlockEvent;
        //更换联盟链公式
        public static event Action<BigInteger, byte[]> ChangeBookKeeperEvent;

        [DisplayName("event")]
        public static event Action<string> notify;

        #region admin
        public static bool verify() => Runtime.CheckWitness(getOwner());
        public static void update(ByteString nefFile, string manifest)
        {
            if (!verify()) throw new Exception("No authorization.");
            ContractManagement.Update(nefFile, manifest);
        }
        public static bool isOwner(UInt160 scriptHash)
        {
            if (scriptHash == getOwner()) return true;
            return false;
        }
        public static UInt160 getOwner()
        {
            byte[] ownerPrefix1 = new byte[] { 0x04, 0x01 };
            ByteString rawOwner = Storage.Get(Storage.CurrentContext, ownerPrefix1);
            return rawOwner is null ? originOwner : (UInt160)rawOwner;
        }
        public static bool setOwner(UInt160 ownerScriptHash)
        {
            if (!verify()) throw new Exception("No authorization");
            Storage.Put(Storage.CurrentContext, ownerPrefix, ownerScriptHash);
            return true;
        }
        #endregion

        #region Header
        public static byte[] tryDeserializeHeader(byte[] Source)
        {
            Header header = deserializHeader(Source);
            return header.nextBookKeeper;
        }

        /// <summary>
        /// Tested 解析跨链区块头
        /// </summary>
        /// <param name="Source"></param>
        /// <returns></returns>
        private static Header deserializHeader(byte[] Source)
        {
            Header header = new Header();
            try
            {
                int offset = 0;
                //get version
                header.version = new BigInteger(Source.Range(offset, 4));
                offset += 4;
                //get chainID
                header.chainId = new BigInteger(Source.Range(offset, 8));
                offset += 8;
                //get prevBlockHash, Hash
                header.prevBlockHash = readHash(Source, offset);
                offset += 32;
                //get transactionRoot, Hash
                header.transactionRoot = readHash(Source, offset);
                offset += 32;
                //get crossStatesRoot, Hash
                header.crossStatesRoot = readHash(Source, offset);
                offset += 32;
                //get blockRoot, Hash
                header.blockRoot = readHash(Source, offset);
                offset += 32;
                //get timeStamp,uint32
                header.timeStamp = new BigInteger(Source.Range(offset, 4));
                offset += 4;
                //get height
                header.height = new BigInteger(Source.Range(offset, 4));
                offset += 4;
                //get consensusData
                header.ConsensusData = new BigInteger(Source.Range(offset, 8));
                offset += 8;
                //get consensysPayload
                ByteString rawPayload;
                (rawPayload, offset) = readVarBytes(Source, offset);
                header.consensusPayload = (byte[])rawPayload;
                //get nextBookKeeper
                header.nextBookKeeper = Source.Range(offset, 20);
            }
            catch
            {
                notify("deserialize header failed");
                throw new ArgumentException();
            }
            notify("deserialize header success");
            return header;
        }
        #endregion

        #region BookKeeper
        public static object getBookKeepers()
        {
            ECPoint[] keepers = (ECPoint[])StdLib.Deserialize(Storage.Get(Storage.CurrentContext, mCKeeperPubKeysPrefix));
            return keepers;
        }
        public static bool changeBookKeeper(byte[] rawHeader, byte[] pubKeyList, byte[] signList)
        {
            Header header = deserializHeader(rawHeader);
            if (header.height == 0)
            {
                return initGenesisBlock(header, pubKeyList);
            }
            BigInteger latestHeight = new BigInteger(((byte[])Storage.Get(Storage.CurrentContext, currentEpochHeightPrefix)).Concat(new byte[] { 0x00 }));
            if (latestHeight > header.height)
            {
                notify("The height of header illegal");
                return false;
            }
            ECPoint[] keepers = (ECPoint[])StdLib.Deserialize(Storage.Get(Storage.CurrentContext, mCKeeperPubKeysPrefix));
            if (!verifySigWithOrder(rawHeader, signList, keepers))
            {
                notify("Verify signature failed");
                return false;
            }
            BookKeeper bookKeeper = verifyPubkey(pubKeyList);
            if ((ByteString)header.nextBookKeeper != (ByteString)bookKeeper.nextBookKeeper)
            {
                notify("bookKeeper not match");
                throw new Exception();
            }
            Storage.Put(Storage.CurrentContext, currentEpochHeightPrefix, header.height);
            Storage.Put(Storage.CurrentContext, mCKeeperPubKeysPrefix, StdLib.Serialize(bookKeeper.keepers));
            ChangeBookKeeperEvent(header.height, rawHeader);
            return true;
        }
        public static bool isGenesised()
        {
            var value = Storage.Get(Storage.CurrentContext, isGenesisedKey);
            return value is null ? false : true;
        }
        public static bool initGenesisBlock(Header header, byte[] pubKeyList)
        {
            if (isGenesised()) return false;
            if (pubKeyList.Length % MCCHAIN_PUBKEY_LEN != 0)
            {
                notify("Length of pubKeyList is illegal");
                throw new ArgumentException();
            }
            BookKeeper bookKeeper = verifyPubkey(pubKeyList);
            Storage.Put(Storage.CurrentContext, currentEpochHeightPrefix, header.height);
            Storage.Put(Storage.CurrentContext, isGenesisedKey, 1);
            Storage.Put(Storage.CurrentContext, mCKeeperPubKeysPrefix, StdLib.Serialize(bookKeeper.keepers));
            return true;
        }
        public static byte[] tryVerifyPubkey(byte[] pubKeyList)
        {
            var bookKeeper = verifyPubkey(pubKeyList);
            return bookKeeper.nextBookKeeper;
        }
        /// <summary>
        /// Tested, 用于将pubKeyList生成BookKeeper
        /// </summary>
        /// <param name="pubKeyList"></param>
        /// <returns></returns>
        private static BookKeeper verifyPubkey(byte[] pubKeyList)
        {
            if (pubKeyList.Length % MCCHAIN_PUBKEY_LEN != 0)
            {
                notify("pubKeyList length illegal");
                throw new ArgumentOutOfRangeException();
            }
            int n = pubKeyList.Length / MCCHAIN_PUBKEY_LEN;
            int m = n - (n - 1) / 3;

            return getBookKeeper(n, m, pubKeyList);
        }
        /// <summary>
        /// Tested, 根据给定的有效签名数与公钥数生成BookKeeper
        /// </summary>
        /// <param name="keyLength"></param>
        /// <param name="m"></param>
        /// <param name="pubKeyList"></param>
        /// <returns></returns>
        private static BookKeeper getBookKeeper(int keyLength, int m, byte[] pubKeyList)
        {
            BookKeeper bookKeeper = new BookKeeper();
            byte[] buff = new byte[] { };
            buff = writeUint16(buff, keyLength);
            ECPoint[] keepers = new ECPoint[keyLength];
            for (int i = 0; i < keyLength; i++)
            {
                var compressPubKey = compressMCPubKey(pubKeyList.Range(i * MCCHAIN_PUBKEY_LEN, MCCHAIN_PUBKEY_LEN));
                buff = writeVarBytes(buff, compressPubKey);
                ECPoint hash = getCompressPubKey(compressPubKey);
                keepers[i] = hash;
            }
            buff = writeUint16(buff, m);
            bookKeeper.nextBookKeeper = bytesToBytes20(Hash160(buff));
            bookKeeper.keepers = keepers;
            return bookKeeper;
        }
        /// <summary>
        /// Tested, 将非压缩型公钥转换至指定曲线压缩型公钥， 但保留前缀
        /// 1205042092e34e0176dccf8abb496b833d591d25533469b3caf0e279b9742955dd8fc3899a042cd338e82698b5284720f85b309f2b711c05cb37836488371741168da6
        /// =>
        /// 1205022092E34E0176DCCF8ABB496B833D591D25533469B3CAF0E279B9742955DD8FC3
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static byte[] compressMCPubKey(byte[] key)
        {
            if (key.Length < 34) return key;
            int index = 2;
            byte a = 0x02;
            byte b = 0x03;
            byte[] newkey = key.Range(0, 35);
            byte[] point = key.Range(66, 1);
            if (new BigInteger(point) % 2 == 0)
            {
                newkey[index] = a;
            }
            else
            {
                newkey[index] = b;
            }
            return newkey;
        }
        /// <summary>
        /// Tested, 移除来自poly上的ECpoint奇怪的前缀
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static ECPoint getCompressPubKey(byte[] key)
        {
            var point = (ECPoint)key.Range(2, 33);
            return point;
        }
        #endregion

        #region Main
        public static bool crossChain(BigInteger toChainID, byte[] toChainAddress, byte[] functionName, byte[] args, byte[] caller)
        {
            var tx = (Transaction)Runtime.ScriptContainer;
            var IdAsByte = toChainID.ToByteArray();
            CrossChainTxParameter para = new CrossChainTxParameter
            {
                toChainID = IdAsByte,
                toContract = toChainAddress,
                method = functionName,
                args = args,

                txHash = (byte[])tx.Hash,
                crossChainID = (byte[])CryptoLib.Sha256((ByteString)((byte[])Runtime.ExecutingScriptHash).Concat((byte[])tx.Hash)),
                fromContract = caller
            };
            BigInteger requestId = getRequestID(IdAsByte);
            var resquestKey = putRequest(IdAsByte, requestId, para);

            //event
            CrossChainLockEvent(caller, para.fromContract, toChainID, resquestKey, para.args);
            return true;
        }
        public static bool verifyAndExecuteTx(byte[] proof, byte[] RawHeader, byte[] headerProof, byte[] currentRawHeader, byte[] signList)
        {
            Header txheader;
            try
            {
                txheader = deserializHeader(RawHeader);
            }
            catch
            {
                notify("Header deserialize failed");
                throw new Exception();
            }
            ECPoint[] keepers = (ECPoint[])StdLib.Deserialize(Storage.Get(Storage.CurrentContext, mCKeeperPubKeysPrefix));
            int n = keepers.Length;
            int m = n - (n - 1) / 3;
            BigInteger currentEpochHeight = (BigInteger)Storage.Get(Storage.CurrentContext, currentEpochHeightPrefix);
            byte[] StateRootValue = new byte[] { 0x00 };
            if (txheader.height >= currentEpochHeight)
            {
                notify("New Tx executing");
                if (!verifySigWithOrder(RawHeader, signList, keepers))
                {
                    notify("Verify RawHeader signature failed!");
                    return false;
                }
            }
            else
            {
                notify("Old Tx");
                Header currentHeader;
                if (!verifySigWithOrder(currentRawHeader, signList, keepers))
                {
                    notify("Verify currentRawHeader signature failed!");
                    return false;
                }
                try
                {
                    currentHeader = deserializHeader(currentRawHeader);
                }
                catch
                {
                    notify("CR Header deserialize Failed");
                    throw new Exception();
                }
                StateRootValue = merkleProve(headerProof, currentHeader.blockRoot);
                ByteString RawHeaderHash = (ByteString)Hash256(RawHeader);
                if (!StateRootValue.Equals(RawHeaderHash))
                {
                    notify("Verify block proof signature failed!");
                    return false;
                }
            }
            // Through rawHeader.CrossStateRoot, the toMerkleValue or cross chain msg can be verified and parsed from proof
            StateRootValue = merkleProve(proof, txheader.crossStatesRoot);
            ToMerkleValue merkleValue = deserializMerkleValue(StateRootValue);
            //check by txid
            if ((BigInteger)Storage.Get(Storage.CurrentContext, transactionPrefix.Concat(merkleValue.fromChainID).Concat(merkleValue.txHash)) == 1)
            {
                notify("Transaction has been executed");
                return false;
            }
            //check to chainID
            if ((ByteString)merkleValue.TxParam.toChainID != (ByteString)chainID)
            {
                notify((ByteString)merkleValue.TxParam.toChainID);
                notify((ByteString)chainID);
                notify("Not Neo crosschain tx");
                return false;
            }
            //run croos chain tx
            if (ExecuteCrossChainTx(merkleValue))
            {
                notify("Tx execute success");
            }
            else
            {
                notify("Tx execute fail");
                return false;
            }

            //event
            CrossChainUnlockEvent(merkleValue.fromChainID, merkleValue.TxParam.toContract, merkleValue.txHash);
            return true;
        }
        private static bool ExecuteCrossChainTx(ToMerkleValue value)
        {
            if (value.TxParam.toContract.Length == 20)
            {
                UInt160 TargetContract = (UInt160)value.TxParam.toContract;
                string TargetMethod = (string)(ByteString)value.TxParam.method;
                object[] parameter = new object[] { value.TxParam.args, value.TxParam.fromContract, value.fromChainID};
                bool DynamicCallResult = false;
                try
                {
                    DynamicCallResult = (bool)Contract.Call(TargetContract, TargetMethod, CallFlags.All, parameter);
                }
                catch
                {
                    notify("Dynamic Call Fail");
                }
                if (DynamicCallResult)
                {
                    Storage.Put(Storage.CurrentContext, transactionPrefix.Concat(value.fromChainID).Concat(value.txHash), 1);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                notify("Contract length is not correct");
                return false;
            }
        }
        private static BigInteger getRequestID(byte[] IdAsBytes)
        {
            ByteString requestID = Storage.Get(Storage.CurrentContext, requestIDPrefix.Concat(IdAsBytes));
            return requestID is null ? 0 : (BigInteger)requestID;
        }
        private static byte[] putRequest(byte[] chainID, BigInteger requestID, CrossChainTxParameter para)
        {
            byte[] requestKey = requestPreifx.Concat(chainID).Concat(requestID.ToByteArray());
            Storage.Put(Storage.CurrentContext, requestKey, WriteCrossChainTxParameter(para));
            requestID = requestID + 1;
            Storage.Put(Storage.CurrentContext, requestIDPrefix.Concat(chainID), requestID);
            return requestKey;
        }
        #endregion

        #region 验签
        /// <summary>
        /// Tested， 校验rawHeader的签名是否满足条件
        /// </summary>
        /// <param name="rawHeader"></param>
        /// <param name="signList"></param>
        /// <param name="keepers"></param>
        /// <returns></returns>
        public static bool verifySigWithOrder(byte[] rawHeader, byte[] signList, ECPoint[] keepers)
        {
            ByteString message = (ByteString)Hash256(rawHeader);
            int m = signList.Length / MCCHAIN_SIGNATURE_LEN;
            int n = keepers.Length;
            bool fSuccess = true;
            for (int i = 0, j = 0; fSuccess && i < m && j < n;)
            {
                ByteString signedMessage = (ByteString)signList.Range(i * MCCHAIN_SIGNATURE_LEN, MCCHAIN_SIGNATUREWITHOUTV_LEN);
                try
                {
                    if (CryptoLib.VerifyWithECDsa(message, keepers[j], signedMessage, NamedCurve.secp256k1))
                        i++;
                    j++;
                    if (m - i > n - j)
                        fSuccess = false;
                }
                catch
                {
                    notify("verifySig exception");
                }
            }
            return fSuccess;
        }

        public static bool verifySigWithOrderForHashTest(byte[] rawHeader, byte[] signList)
        {
            ECPoint[] keepers = (ECPoint[])StdLib.Deserialize(Storage.Get(Storage.CurrentContext, mCKeeperPubKeysPrefix));
            notify("Keepers: " + keepers.Length);
            ByteString message = (ByteString)Hash256(rawHeader);
            int m = signList.Length / MCCHAIN_SIGNATURE_LEN;
            int n = keepers.Length;
            bool fSuccess = true;
            for (int i = 0, j = 0; fSuccess && i < m && j < n;)
            {

                ByteString signedMessage = (ByteString)signList.Range(i * MCCHAIN_SIGNATURE_LEN, MCCHAIN_SIGNATUREWITHOUTV_LEN);
                try
                {
                    if (CryptoLib.VerifyWithECDsa(message, keepers[j], signedMessage, NamedCurve.secp256k1))
                        i++;
                    j++;
                    if (m - i > n - j)
                    {
                        fSuccess = false;
                    }
                }
                catch
                {
                    notify("verifySig exception");
                }
                finally
                {
                    notify("Number: " + m + i + n + j);
                }
            }
            return fSuccess;
        }
        #endregion

        #region crossChain IO
        private static byte[] WriteCrossChainTxParameter(CrossChainTxParameter para)
        {
            byte[] result = new byte[] { };
            result = writeVarBytes(result, para.txHash);
            result = writeVarBytes(result, para.crossChainID);
            result = writeVarBytes(result, para.fromContract);
            byte[] toChainIDBytes = padRight(para.toChainID, 8);
            result = result.Concat(toChainIDBytes);
            result = writeVarBytes(result, para.toContract);
            result = writeVarBytes(result, para.method);
            result = writeVarBytes(result, para.args);
            return result;
        }

        public static bool tryDeserializeMerkleValue(byte[] Source)
        {
            try
            {
                ToMerkleValue result = deserializMerkleValue(Source);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tested, 反序列化MerkleValue
        /// </summary>
        /// <param name="Source"></param>
        /// <returns></returns>
        private static ToMerkleValue deserializMerkleValue(byte[] Source)
        {
            int offset = 0;
            try
            {
                ToMerkleValue result = new ToMerkleValue();
                //get txHash
                ByteString rawTxHash;
                (rawTxHash, offset) = readVarBytes(Source, offset);
                result.txHash = (byte[])rawTxHash;

                //get fromChainID, Uint64
                result.fromChainID = Source.Range(offset, 8);
                offset = offset + 8;

                //get CrossChainTxParameter
                result.TxParam = deserializCrossChainTxParameter(Source, offset);
                return result;
            }
            catch
            {
                throw new Exception();
            }

        }
        /// <summary>
        /// Tested, 反序列化跨链交易参数
        /// </summary>
        /// <param name="Source"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        private static CrossChainTxParameter deserializCrossChainTxParameter(byte[] Source, int offset)
        {
            CrossChainTxParameter txParameter = new CrossChainTxParameter();
            //get txHash
            ByteString rawTxParameter;
            (rawTxParameter, offset) = readVarBytes(Source, offset);
            txParameter.txHash = (byte[])rawTxParameter;

            //get crossChainId
            ByteString rawCrossChainId;
            (rawCrossChainId, offset) = readVarBytes(Source, offset);
            txParameter.crossChainID = (byte[])rawCrossChainId;

            //get fromContract
            ByteString rawFromContract;
            (rawFromContract, offset) = readVarBytes(Source, offset);
            txParameter.fromContract = (byte[])rawFromContract;

            //get toChainID
            txParameter.toChainID = Source.Range(offset, 8);
            offset = offset + 8;

            //get toContract
            ByteString rawToContract;
            (rawToContract, offset) = readVarBytes(Source, offset);
            txParameter.toContract = (byte[])rawToContract;

            //get method
            ByteString rawMethod;
            (rawMethod, offset) = readVarBytes(Source, offset);
            txParameter.method = (byte[])rawMethod;

            //get params
            ByteString rawArgs;
            (rawArgs, offset) = readVarBytes(Source, offset);
            txParameter.args = (byte[])rawArgs;
            return txParameter;
        }
        #endregion

        #region Merkle Method
        public static byte[] merkleProve(byte[] path, byte[] root)
        {
            int offSet = 0;
            ByteString value;
            (value, offSet) = readVarBytes(path, offSet);
            byte[] hash = hashLeaf((byte[])value);
            int size = (path.Length - offSet) / 32;
            for (int i = 0; i < size; i++)
            {
                ByteString isChildren;
                (isChildren, offSet) = readBytes(path, offSet, 1);
                ByteString node;
                (node, offSet) = readBytes(path, offSet, 32);
                if (isChildren == (ByteString)new byte[] { 0 })
                {
                    hash = hashChildren((byte[])node, hash);
                }
                else
                {
                    hash = hashChildren(hash, (byte[])node);
                }
            }
            if (((ByteString)hash).Equals((ByteString)root))
            {
                return (byte[])value;
            }
            else
            {
                throw new Exception("proof not match root");
            }
        }

        /// <summary>
        /// Tested, 对子节点进行Hash计算
        /// </summary>
        /// <param name="v"></param>
        /// <param name="hash"></param>
        /// <returns></returns>
        public static byte[] hashChildren(byte[] v, byte[] hash)
        {
            byte[] prefix = new byte[] { 1 };
            return (byte[])CryptoLib.Sha256((ByteString)prefix.Concat(v).Concat(hash));
        }

        /// <summary>
        /// Tested, 对叶节点进行Hash计算
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static byte[] hashLeaf(byte[] value)
        {
            byte[] prefix = new byte[] { 0x00 };
            return (byte[])CryptoLib.Sha256((ByteString)prefix.Concat(value));
        }
        #endregion
        #region crypto
        public static byte[] Hash256(byte[] message)
        {
            return (byte[])CryptoLib.Sha256(CryptoLib.Sha256((ByteString)message));
        }

        public static byte[] Hash160(byte[] message)
        {
            return (byte[])CryptoLib.ripemd160(CryptoLib.Sha256((ByteString)message));
        }
        #endregion

        #region basicIO
        /// <summary>
        /// Tested, 包含在deserializeHeader中
        /// </summary>
        /// <param name="Source"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        private static byte[] readHash(byte[] Source, int offset)
        {
            if (offset + 32 <= Source.Length)
            {
                return Source.Range(offset, 32);
            }
            throw new ArgumentOutOfRangeException();
        }

        /// <summary>
        /// Tested, 写入uint16. 长度未满足2字节时， 0补足
        /// </summary>
        /// <param name="value"></param>
        /// <param name="Source"></param>
        /// <returns></returns>
        public static byte[] writeUint16(byte[] Source, BigInteger value)
        {
            if (value > UInt16.MaxValue) throw new ArgumentException();
            return Source.Concat(padRight(convertUintToByteArray(value), 2));
        }

        /// <summary>
        /// Tested, 返回Source + Content 的字节，Content包含长度
        /// </summary>
        /// <param name="Source"></param>
        /// <param name="Content"></param>
        /// <returns></returns>
        public static byte[] writeVarBytes(byte[] Source, byte[] Content)
        {
            return writeVarInt(Content.Length, Source).Concat(Content);
        }

        /// <summary>
        /// Tested, 读取带长度的byte[]
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <returns></returns>

        public static (ByteString, int) readVarBytes(byte[] buffer, int offset)
        {
            (var count, var newOffset) = readVarInt(buffer, offset);
            return readBytes(buffer, newOffset, (int)count);
        }

        /// <summary>
        /// Tested, 读取byte[]， 并转成BigInteger
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static (BigInteger, int) readVarInt(byte[] buffer, int offset)
        {
            (ByteString fb, int newOffset) = readBytes(buffer, offset, 1); // read the first byte
            if (fb.Equals((ByteString)new byte[] { 0xfd }))
            {
                return (new BigInteger(buffer.Range(newOffset, 2)), newOffset + 2);
            }
            else if (fb.Equals((ByteString)new byte[] { 0xfe }))
            {
                return (new BigInteger(buffer.Range(newOffset, 4)), newOffset + 4);
            }
            else if (fb.Equals((ByteString)new byte[] { 0xff }))
            {
                return (new BigInteger(buffer.Range(newOffset, 8)), newOffset + 8);
            }
            else
            {
                return (new BigInteger(((byte[])fb).Concat(new byte[] { 0x00 })), newOffset);
            }
        }

        /// <summary>
        /// Tested, 将BigInteger写入指定byte[]
        /// </summary>
        /// <param name="value"></param>
        /// <param name="source"></param>
        /// <returns></returns>
        public static byte[] writeVarInt(BigInteger value, byte[] source)
        {
            if (value < 0)
            {
                notify("WVI: value should be positive");
                throw new ArgumentException();
            }
            else if (value < 0xFD)
            {
                var v = padRight(value.ToByteArray().Reverse().Last(1), 1);
                return source.Concat(v);
            }
            else if (value <= 0xFFFF) // 0xff, need to pad 1 0x00
            {
                byte[] length = new byte[] { 0xFD };
                var v = padRight(value.ToByteArray().Reverse().Last(2), 2);
                return source.Concat(length).Concat(v);
            }
            else if (value <= 0XFFFFFFFF) //0xffffff, need to pad 1 0x00 
            {
                byte[] length = new byte[] { 0xFE };
                var v = padRight(value.ToByteArray().Reverse().Last(4), 4);
                return source.Concat(length).Concat(v);
            }
            else //0x ff ff ff ff ff, need to pad 3 0x00
            {
                byte[] length = new byte[] { 0xFF };
                var v = padRight(value.ToByteArray().Reverse().Last(8), 8);
                return source.Concat(length).Concat(v);
            }
        }

        /// <summary>
        /// Tested, 读取指定字长， 并返回byte[], 与更新的offset
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public static (ByteString, int) readBytes(byte[] buffer, int offset, int count)
        {
            if (offset + count > buffer.Length)
            {
                notify("read Bytes fail");
                throw new ArgumentException();
            }
            return ((ByteString)buffer.Range(offset, count), offset + count);

        }

        /// <summary>
        /// Tested, 将byte数组补齐长度至指定位数
        /// </summary>
        /// <param name="value"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static byte[] padRight(byte[] value, int length)
        {
            var l = value.Length;
            if (l > length)
            {
                notify("size exceed");
                throw new ArgumentException();
            }
            for (int i = 0; i < length - l; i++)
            {
                value = value.Concat(new byte[] { 0x00 });
            }
            return value;
        }

        private static byte[] bytesToBytes20(byte[] Source)
        {
            if (Source.Length != 20)
            {
                throw new ArgumentOutOfRangeException();
            }
            else
            {
                return Source;
            }
        }

        /// <summary>
        /// Tested, 将正Biginteger转换成byteArray,负数抛出异常, 返回结果大端序
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        public static byte[] convertUintToByteArray(BigInteger unsignNumber)
        {
            if (unsignNumber < 0) throw new ArgumentException();
            byte[] resultWithSign = unsignNumber.ToByteArray();
            int length = resultWithSign.Length - 1;
            return resultWithSign.Range(0, length).Reverse();
        }
        #endregion
    }
    public struct ToMerkleValue
    {
        public byte[] txHash;
        public byte[] fromChainID;
        public CrossChainTxParameter TxParam;
    }

    public struct CrossChainTxParameter
    {
        public byte[] toChainID;
        public byte[] toContract;
        public byte[] method;
        public byte[] args;

        public byte[] txHash;
        public byte[] crossChainID;
        public byte[] fromContract;
    }

    public struct Header
    {
        public BigInteger version;//uint32
        public BigInteger chainId;//uint64
        public byte[] prevBlockHash;//Hash
        public byte[] transactionRoot;//Hash  无用
        public byte[] crossStatesRoot;//Hash  用来验证跨链交易
        public byte[] blockRoot;//Hash 用来验证header
        public BigInteger timeStamp;//uint32
        public BigInteger height;//uint32
        public BigInteger ConsensusData;//uint64
        public byte[] consensusPayload;
        public byte[] nextBookKeeper;
    }

    public struct BookKeeper
    {
        public byte[] nextBookKeeper;
        public ECPoint[] keepers;
    }
}

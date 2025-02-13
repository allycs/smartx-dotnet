﻿using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Collections;
using System.Threading.Tasks;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace ETModel
{

    public class Pool : Component
    {
        HttpPool httpPool = null;
        public TransferProcess transferProcess = null;
        public LevelDBStore PoolDBStore = new LevelDBStore();
        public string style = "SOLO";
        public float  serviceFee = 0; // 矿池手续费
        public long   OutTimeDBMiner   = 5760*3;
        public long   OutTimeDBCounted = 100;
        public long   RewardInterval   = 32;

        public override void Awake(JToken jd = null)
        {
            style = jd["style"]?.ToString();
            float.TryParse(jd["serviceFee"]?.ToString(), out serviceFee);
            serviceFee = MathF.Max(0, MathF.Min(1, serviceFee));

            if (style == "PPLNS")
            {
                string db_path = jd["db_path"]?.ToString();
                var DatabasePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), db_path);
                PoolDBStore.Init(DatabasePath);
            }
            if (jd["OutTimeDBMiner"] != null)
                long.TryParse(jd["OutTimeDBMiner"]?.ToString(), out OutTimeDBMiner);
            if (jd["OutTimeDBCounted"] != null)
                long.TryParse(jd["OutTimeDBCounted"]?.ToString(), out OutTimeDBCounted);
            if (jd["RewardInterval"] != null)
                long.TryParse(jd["RewardInterval"]?.ToString(), out RewardInterval);

            httpPool = Entity.Root.GetComponentInChild<HttpPool>();
            transferProcess = Entity.Root.AddComponent<TransferProcess>();
        }

        public override void Start()
        {
            Run();
        }

        public async void Run()
        {
            await Task.Delay(1000);

            while (true)
            {
                await Task.Delay(7500);

                try
                {
                    if (style == "PPLNS")
                    {
                        MinerSave();
                        var minerTransfer = MinerReward_PPLNS();
                    }
                    if (style == "SOLO")
                    {
                        var minerTransfer = MinerReward_SOLO();


                    }
                    ClearOutTimeDB();
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }

        public void MinerSave()
        {
            if (httpPool != null)
            {
                Dictionary<string, MinerTask> miners = httpPool.GetMinerReward(out long miningHeight);
                if (miners != null && miningHeight + 3 < httpPool.height)
                {
                    using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
                    {
                        string json = snapshot.Get("Pool_H_Miner");
                        long height_miner = -1;
                        if (!string.IsNullOrEmpty(json))
                            long.TryParse(json, out height_miner);

                        if (height_miner==-1 || height_miner < miningHeight)
                        {
                            snapshot.Add("Pool_H_Miner", miningHeight.ToString());
                            snapshot.Add("Pool_H_" + miningHeight, JsonHelper.ToJson(miners));
                            snapshot.Commit();
                            httpPool.DelMiner(miningHeight);
                        }
                    }
                }
            }
        }

        public class MinerRewardDB
        {
            public long counted;
            public long minHeight;
            public long maxHeight;
            public string time;
            public string totalPower;
        }

        public Dictionary<string, BlockSub> MinerReward_PPLNS(bool saveDB = true)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            long counted = 0;
            long minHeight = -1;
            long maxHeight = -1;
            using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
            {
                string str_Counted = snapshot.Get("Pool_Counted");
                long.TryParse(str_Counted,out counted);
                string str_MR = snapshot.Get($"Pool_MR_{str_Counted}");
                MinerRewardDB minerRewardLast = null;
                if (!string.IsNullOrEmpty(str_MR))
                    minerRewardLast = JsonHelper.FromJson<MinerRewardDB>(str_MR);
                if (minerRewardLast==null)
                {
                    snapshot.Add("Pool_Counted", "0");
                    var minerRewardNew = new MinerRewardDB();
                    minerRewardNew.counted = 0;
                    minerRewardNew.minHeight = httpPool.height;
                    minerRewardNew.maxHeight = httpPool.height;
                    minerRewardNew.time = TimeHelper.time.ToString();
                    snapshot.Add($"Pool_MR_{0}", JsonHelper.ToJson(minerRewardNew));
                    snapshot.Commit();
                    return null;
                }
                minHeight = minerRewardLast.maxHeight;

                string json = snapshot.Get("Pool_H_Miner");
                if (!string.IsNullOrEmpty(json))
                    long.TryParse(json, out maxHeight);
            }

            if ( maxHeight - minHeight < RewardInterval && saveDB )
                return null;

            var minerTransfer = MinerReward_PPLNS(today, minHeight, maxHeight);

            if (saveDB)
            {
                using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
                {
                    foreach (var it in minerTransfer)
                    {
                        transferProcess.AddTransferHandle(it.Value.addressIn, it.Value.addressOut, it.Value.amount, it.Value.data);
                    }

                    counted += 1;
                    snapshot.Add("Pool_Counted", counted.ToString());
                    var minerRewardNew = new MinerRewardDB();
                    minerRewardNew.counted = counted;
                    minerRewardNew.minHeight = minHeight;
                    minerRewardNew.maxHeight = maxHeight;
                    minerRewardNew.time = DateTime.UtcNow.Ticks.ToString();
                    snapshot.Add($"Pool_MR_{counted}", JsonHelper.ToJson(minerRewardNew));

                    // Pool_MT
                    var depend = new DateTime(DateTime.UtcNow.Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss");
                    foreach (var it in minerTransfer)
                    {
                        it.Value.depend = depend;
                        snapshot.Queue.Push($"Pool_MT_{it.Value.addressOut}", JsonHelper.ToJson(it.Value));
                    }

                    snapshot.Commit();
                }

                transferProcess.SaveTransferToDB();
            }

            return minerTransfer;
        }

        // Miner reward, only after confirming that it cannot be rolled back
        public Dictionary<string, BlockSub> MinerReward_PPLNS(string today,long minHeight,long maxHeight)
        {
            Dictionary<string, BlockSub> minerTransfer = new Dictionary<string, BlockSub>();
            if (httpPool != null)
            {
                WalletKey walletKey = Wallet.GetWallet().GetCurWallet();
                for (long rewardheight = minHeight; rewardheight < maxHeight; rewardheight++)
                {
                    Dictionary<string, MinerTask> miners = null;
                    using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
                    {
                        string json = snapshot.Get("Pool_H_" + rewardheight);
                        if (!string.IsNullOrEmpty(json)) { 
                            miners = JsonHelper.FromJson<Dictionary<string, MinerTask>>(json);
                        }
                    }

                    //var miners = httpPool.GetMiner(rewardheight);
                    if (miners!= null)
                    {
                        string ownerAddress = Wallet.GetWallet().GetCurWallet().ToAddress();

                        var mcblk = BlockChainHelper.GetMcBlock(rewardheight);
                        if (mcblk != null && mcblk.Address == ownerAddress)
                        {
                            BigFloat reward = new BigFloat(Consensus.GetReward(rewardheight));
                            reward = reward * (1.0f-serviceFee);

                            var miner = miners.Values.FirstOrDefault(c => c.random == mcblk.random);
                            if (miner == null)
                            {
                                continue;
                            }

                            // Total power
                            BigFloat diffsum = new BigFloat(0);
                            foreach (var dic in miners.Values)
                            {
                                if (string.IsNullOrEmpty(dic.address))
                                    continue;
                                if (dic.diff < 0.99999f)
                                    continue;
                                diffsum += new BigFloat(dic.diff);
                            }

                            // Reward for participation
                            foreach (var dic in miners.Values)
                            {
                                if (string.IsNullOrEmpty(dic.address))
                                    continue;
                                if (dic.diff < 0.99999f)
                                    continue;

                                var v = new BigFloat(dic.diff);
                                string pay = BigHelper.Round8((v * reward / diffsum).ToString());

                                if (minerTransfer.TryGetValue(dic.address, out BlockSub transfer))
                                {
                                    transfer.amount = BigHelper.Add(transfer.amount, pay);
                                }
                                else
                                if(BigHelper.Greater(pay, "0.002",false))
                                {
                                    transfer = new BlockSub();
                                    transfer.addressIn = ownerAddress;
                                    transfer.addressOut = dic.address;
                                    transfer.amount = BigHelper.Sub(pay,"0.002"); // 扣除交易手续费
                                    transfer.type = "transfer";
                                    transfer.data = CryptoHelper.Sha256($"{today}_{maxHeight}_{ownerAddress}_{dic.address}_MinerReward");
                                    minerTransfer.Add(transfer.addressOut, transfer);
                                }
                            }

                        }
                    }
                }
            }
            return minerTransfer;
        }

        public Dictionary<string, BlockSub> MinerReward_SOLO()
        {
            if (httpPool != null)
            {
                Dictionary<string, BlockSub> minerTransfer = new Dictionary<string, BlockSub>();
                Dictionary<string, MinerTask> miners = httpPool.GetMinerReward(out long miningHeight);
                if (miners != null && miningHeight + 5 < httpPool.height)
                {
                    string ownerAddress = Wallet.GetWallet().GetCurWallet().ToAddress();

                    var mcblk = BlockChainHelper.GetMcBlock(miningHeight);
                    if (mcblk != null && mcblk.Address == ownerAddress)
                    {
                        var miner = miners.Values.FirstOrDefault(c => c.random == mcblk.random);
                        if (miner != null)
                        {
                            var transfer = new BlockSub();
                            transfer.addressIn  = ownerAddress;
                            transfer.addressOut = miner.address;
                            transfer.type   = "transfer";
                            transfer.amount = Consensus.GetReward(miningHeight).ToString();
                            transfer.data   = CryptoHelper.Sha256($"{mcblk.hash}_{ownerAddress}_{miner.address}_{transfer.amount}_Reward_SOLO");
                            minerTransfer.Add(transfer.addressOut, transfer);

                            transferProcess.AddTransferHandle(ownerAddress, miner.address, transfer.amount, transfer.data);
                            transferProcess.SaveTransferToDB();
                        }
                    }
                    httpPool.DelMiner(miningHeight);
                    return minerTransfer;
                }
            }
            return null;
        }

        public class MinerView
        {
            public string address;
            public string amount_cur;
            public string totalPower;
            public long   totalMiners;
            public List<BlockSub>      transfers = new List<BlockSub>();
            public List<MinerViewData>    miners = new List<MinerViewData>();
        }

        public class MinerViewData
        {
            public string number;
            public string power_cur;
            public string power_average;
            public float  lasttime;

        }

        public MinerView GetMinerView(string address,long transferIndex,long transferColumn, long minerIndex,  long minerColumn)
        {
            transferColumn = Math.Min(transferColumn, 100);
            minerColumn    = Math.Min(minerColumn, 100);

            var minerView = new MinerView();
            minerView.address = address;

            var transfers_cur  = MinerReward_PPLNS(false)?.Values.FirstOrDefault(c => c.addressOut == address);
            if(transfers_cur!=null)
            {
                minerView.amount_cur = transfers_cur.amount;
            }

            using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
            {
                int TopIndex = snapshot.Queue.GetTopIndex($"Pool_MT_{address}");
                for (int ii = 1; ii <= (int)transferColumn; ii++)
                {
                    var value = snapshot.Queue.Get($"Pool_MT_{address}", TopIndex - (int)transferIndex - ii);
                    if (!string.IsNullOrEmpty(value))
                    {
                        var transfer = JsonHelper.FromJson<BlockSub>(value);
                        if (transfer != null)
                        {
                            minerView.transfers.Add(transfer);
                        }
                    }
                }

                foreach (var transfer in minerView.transfers)
                {
                    // 节点使用自己的地址挖矿
                    if (transfer.addressIn == transfer.addressOut)
                        transfer.hash = transfer.addressIn;
                    else
                        transfer.hash = transferProcess.GetMinerTansfer(snapshot, transfer.data);
                }
            }

            var miners = httpPool.GetMinerReward(out long miningHeight);
            var minerList = miners?.Values.Where((x) => x.address == address).ToList();

            double totalPower = 0L;

            if (minerList != null) {

                minerList.Sort((MinerTask a, MinerTask b) => {
                    return a.number.CompareTo(b.number);
                });

                for (var ii = 0; ii < minerColumn; ii++)
                {
                    if ((minerIndex + ii) >= minerList.Count)
                        break;
                    var miner = minerList[(int)minerIndex + ii];

                    if (string.IsNullOrEmpty(miner.power_cur))
                        miner.power_cur = CalculatePower.GetPowerCompany(CalculatePower.Power(miner.diff));

                    var minerdata = new MinerViewData();
                    minerdata.number = miner.number;
                    minerdata.lasttime = miner.time;
                    minerdata.power_cur = miner.power_cur;

                    double.TryParse(miner.power_average, out double power_average);
                    minerdata.power_average = CalculatePower.GetPowerCompany(power_average);
                    minerView.miners.Add(minerdata);

                    totalPower += power_average;
                }
                minerView.totalMiners = minerList.Count;
            }

            minerView.totalPower  = CalculatePower.GetPowerCompany(totalPower);

            return minerView;
        }

        public int GetTransfersCount()
        {
            return transferProcess.transfers.Count;
        }


        public void ClearOutTimeDB()
        {
            using (DbSnapshot snapshot = PoolDBStore.GetSnapshot())
            {
                // Miner
                string json = snapshot.Get("Pool_H_Miner");
                long height_miner = -1;
                if (!string.IsNullOrEmpty(json))
                    long.TryParse(json, out height_miner);

                for (long ii = OutTimeDBMiner; ii < OutTimeDBMiner*3; ii++)
                {
                    string key = "Pool_H_" + (height_miner - ii);
                    if (!string.IsNullOrEmpty(snapshot.Get(key)))
                        snapshot.Delete("Pool_H_" + (height_miner - ii));
                    else
                        break;
                }

                long counted = 0;
                string str_Counted = snapshot.Get("Pool_Counted");
                long.TryParse(str_Counted, out counted);
                for (long ii = OutTimeDBCounted; ii < OutTimeDBCounted*3; ii++)
                {
                    long index = counted - ii;
                    if (!string.IsNullOrEmpty(snapshot.Get($"Pool_MR_{index}")))
                    {
                        snapshot.Delete($"Pool_MR_{index}");
                    }
                    else
                        break;
                }
                snapshot.Commit();
            }

        }

    }


}























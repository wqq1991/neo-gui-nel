﻿using Neo.Core;
using Neo.Cryptography.ECC;
using Neo.SmartContract;
using Neo.IO.Caching;
using Neo.VM;
using System;
using System.Numerics;
using System.Text;
using Neo.SmartContract.Debug;
using System.Linq;
using System.Collections.Generic;

namespace Neo.SmartContract
{
    public class ApplicationEngine : ExecutionEngine
    {
        #region Limits
        /// <summary>
        /// Set the max size allowed size for BigInteger
        /// </summary>
        private const int MaxSizeForBigInteger = 32;
        /// <summary>
        /// Set the max Stack Size
        /// </summary>
        private const uint MaxStackSize = 2 * 1024;
        /// <summary>
        /// Set Max Item Size
        /// </summary>
        private const uint MaxItemSize = 1024 * 1024;
        /// <summary>
        /// Set Max Invocation Stack Size
        /// </summary>
        private const uint MaxInvocationStackSize = 1024;
        /// <summary>
        /// Set Max Array Size
        /// </summary>
        private const uint MaxArraySize = 1024;
        #endregion

        private const long ratio = 100000;
        private const long gas_free = 10 * 100000000;
        private readonly long gas_amount;
        private long gas_consumed = 0;
        private readonly bool testMode;

        private readonly CachedScriptTable script_table;

        public TriggerType Trigger { get; }
        public Fixed8 GasConsumed => new Fixed8(gas_consumed);

        public ApplicationEngine(TriggerType trigger, IScriptContainer container, IScriptTable table, InteropService service, Fixed8 gas, bool testMode = false)
            : base(container, Cryptography.Crypto.Default, table, service)
        {
            this.gas_amount = gas_free + gas.GetData();
            this.testMode = testMode;
            this.Trigger = trigger;
            if (table is CachedScriptTable)
            {
                this.script_table = (CachedScriptTable)table;
            }
        }

        private bool CheckArraySize(OpCode nextInstruction)
        {
            switch (nextInstruction)
            {
                case OpCode.PACK:
                case OpCode.NEWARRAY:
                case OpCode.NEWSTRUCT:
                    {
                        if (EvaluationStack.Count == 0) return false;
                        int size = (int)EvaluationStack.Peek().GetBigInteger();
                        if (size > MaxArraySize) return false;
                        return true;
                    }
                default:
                    return true;
            }
        }

        private bool CheckInvocationStack(OpCode nextInstruction)
        {
            switch (nextInstruction)
            {
                case OpCode.CALL:
                case OpCode.APPCALL:
                    if (InvocationStack.Count >= MaxInvocationStackSize) return false;
                    return true;
                default:
                    return true;
            }
        }

        private bool CheckItemSize(OpCode nextInstruction)
        {
            switch (nextInstruction)
            {
                case OpCode.PUSHDATA4:
                    {
                        if (CurrentContext.InstructionPointer + 4 >= CurrentContext.Script.Length)
                            return false;
                        uint length = CurrentContext.Script.ToUInt32(CurrentContext.InstructionPointer + 1);
                        if (length > MaxItemSize) return false;
                        return true;
                    }
                case OpCode.CAT:
                    {
                        if (EvaluationStack.Count < 2) return false;
                        int length = EvaluationStack.Peek(0).GetByteArray().Length + EvaluationStack.Peek(1).GetByteArray().Length;
                        if (length > MaxItemSize) return false;
                        return true;
                    }
                default:
                    return true;
            }
        }

        /// <summary>
        /// Check if the BigInteger is allowed for numeric operations
        /// </summary>
        /// <param name="value">Value</param>
        /// <returns>Return True if are allowed, otherwise False</returns>
        private bool CheckBigInteger(BigInteger value)
        {
            return value == null ? false :
                value.ToByteArray().Length <= MaxSizeForBigInteger;
        }

        /// <summary>
        /// Check if the BigInteger is allowed for numeric operations
        /// </summary> 
        private bool CheckBigIntegers(OpCode nextInstruction)
        {
            switch (nextInstruction)
            {
                case OpCode.INC:
                    {
                        BigInteger x = EvaluationStack.Peek().GetBigInteger();

                        if (!CheckBigInteger(x) || !CheckBigInteger(x + 1))
                            return false;

                        break;
                    }
                case OpCode.DEC:
                    {
                        BigInteger x = EvaluationStack.Peek().GetBigInteger();

                        if (!CheckBigInteger(x) || (x.Sign <= 0 && !CheckBigInteger(x - 1)))
                            return false;

                        break;
                    }
                case OpCode.ADD:
                    {
                        BigInteger x2 = EvaluationStack.Peek().GetBigInteger();
                        BigInteger x1 = EvaluationStack.Peek(1).GetBigInteger();

                        if (!CheckBigInteger(x2) || !CheckBigInteger(x1) || !CheckBigInteger(x1 + x2))
                            return false;

                        break;
                    }
                case OpCode.SUB:
                    {
                        BigInteger x2 = EvaluationStack.Peek().GetBigInteger();
                        BigInteger x1 = EvaluationStack.Peek(1).GetBigInteger();

                        if (!CheckBigInteger(x2) || !CheckBigInteger(x1) || !CheckBigInteger(x1 - x2))
                            return false;

                        break;
                    }
                case OpCode.MUL:
                    {
                        BigInteger x2 = EvaluationStack.Peek().GetBigInteger();
                        BigInteger x1 = EvaluationStack.Peek(1).GetBigInteger();

                        int lx1 = x1 == null ? 0 : x1.ToByteArray().Length;

                        if (lx1 > MaxSizeForBigInteger)
                            return false;

                        int lx2 = x2 == null ? 0 : x2.ToByteArray().Length;

                        if ((lx1 + lx2) > MaxSizeForBigInteger)
                            return false;

                        break;
                    }
                case OpCode.DIV:
                    {
                        BigInteger x2 = EvaluationStack.Peek().GetBigInteger();
                        BigInteger x1 = EvaluationStack.Peek(1).GetBigInteger();

                        if (!CheckBigInteger(x2) || !CheckBigInteger(x1))
                            return false;

                        break;
                    }
                case OpCode.MOD:
                    {
                        BigInteger x2 = EvaluationStack.Peek().GetBigInteger();
                        BigInteger x1 = EvaluationStack.Peek(1).GetBigInteger();

                        if (!CheckBigInteger(x2) || !CheckBigInteger(x1))
                            return false;

                        break;
                    }
            }

            return true;
        }

        private bool CheckStackSize(OpCode nextInstruction)
        {
            int size = 0;
            if (nextInstruction <= OpCode.PUSH16)
                size = 1;
            else
                switch (nextInstruction)
                {
                    case OpCode.DEPTH:
                    case OpCode.DUP:
                    case OpCode.OVER:
                    case OpCode.TUCK:
                        size = 1;
                        break;
                    case OpCode.UNPACK:
                        StackItem item = EvaluationStack.Peek();
                        if (!item.IsArray) return false;
                        size = item.GetArray().Count;
                        break;
                }
            if (size == 0) return true;
            size += EvaluationStack.Count + AltStack.Count;
            if (size > MaxStackSize) return false;
            return true;
        }

        private bool CheckDynamicInvoke(OpCode nextInstruction)
        {
            if (nextInstruction == OpCode.APPCALL || nextInstruction == OpCode.TAILCALL)
            {
                for (int i = CurrentContext.InstructionPointer + 1; i < CurrentContext.InstructionPointer + 21; i++)
                {
                    if (CurrentContext.Script[i] != 0) return true;
                }
                // if we get this far it is a dynamic call
                // now look at the current executing script
                // to determine if it can do dynamic calls
                ContractState contract = script_table.GetContractState(CurrentContext.ScriptHash);
                return contract.HasDynamicInvoke;
            }
            return true;
        }

        public new bool Execute()
        {
            try
            {
                while (!State.HasFlag(VMState.HALT) && !State.HasFlag(VMState.FAULT))
                {
                    OpCode nextOpcode = OpCode.NOP;
                    if (CurrentContext.InstructionPointer < CurrentContext.Script.Length)
                    {
                        nextOpcode = CurrentContext.NextInstruction;

                        if (this.FullLog != null)
                        {
                            this.FullLog.NextOp(CurrentContext.InstructionPointer, nextOpcode);
                            this.EvaluationStack.ClearRecord();
                        }
                        gas_consumed = checked(gas_consumed + GetPrice(nextOpcode) * ratio);
                        if (!testMode && gas_consumed > gas_amount)
                        {
                            if (FullLog != null) FullLog.Error("gas_consumed > gas_amount");
                            return false;
                        }
                        if (!CheckItemSize(nextOpcode))
                        {
                            if (FullLog != null) FullLog.Error("CheckItemSize");
                            return false;
                        }
                        if (!CheckStackSize(nextOpcode))
                        {
                            if (FullLog != null) FullLog.Error("CheckStackSize");
                            return false;
                        }
                        if (!CheckArraySize(nextOpcode))
                        {
                            if (FullLog != null) FullLog.Error("CheckArraySize");
                            return false;
                        }
                        if (!CheckInvocationStack(nextOpcode))
                        {
                            if (FullLog != null) FullLog.Error("CheckInvocationStack");
                            return false;
                        }
                        if (!CheckBigIntegers(nextOpcode))
                        {
                            if (FullLog != null) FullLog.Error("CheckBigIntegers");
                            return false;
                        }
                    }
                    StepInto();
                    if (FullLog != null)
                    {
                        VM.StackItem result = null;
                        ExecutionStackRecord.Op[] record = this.EvaluationStack.record.ToArray();
                        var ltype = this.EvaluationStack.GetLastRecordType();
                        if (ltype == ExecutionStackRecord.OpType.Push)
                        {
                            result = this.EvaluationStack.PeekWithoutLog();
                        }
                        else if (ltype == ExecutionStackRecord.OpType.Insert)
                        {
                            result = this.EvaluationStack.PeekWithoutLog(this.EvaluationStack.record.Last().ind);
                        }
                        else if (ltype == ExecutionStackRecord.OpType.Set)
                        {
                            result = this.EvaluationStack.PeekWithoutLog(this.EvaluationStack.record.Last().ind);
                        }
                        LogResult(nextOpcode, record, result);
                    }
                }
            }
            catch(Exception err)
            {
                return false;
            }
            if (FullLog != null)
            {
                FullLog.Finish(State);
            }
            return !State.HasFlag(VMState.FAULT);
        }
        public override void SetParam(OpCode opcode, byte[] opdata)
        {
            if(this.FullLog!=null)
                this.FullLog.SetParam(opcode, opdata);
        }
        protected virtual long GetPrice(OpCode nextInstruction)
        {
            if (nextInstruction <= OpCode.PUSH16) return 0;
            switch (nextInstruction)
            {
                case OpCode.NOP:
                    return 0;
                case OpCode.APPCALL:
                case OpCode.TAILCALL:
                    return 10;
                case OpCode.SYSCALL:
                    return GetPriceForSysCall();
                case OpCode.SHA1:
                case OpCode.SHA256:
                    return 10;
                case OpCode.HASH160:
                case OpCode.HASH256:
                    return 20;
                case OpCode.CHECKSIG:
                    return 100;
                case OpCode.CHECKMULTISIG:
                    {
                        if (EvaluationStack.Count == 0) return 1;
                        int n = (int)EvaluationStack.Peek().GetBigInteger();
                        if (n < 1) return 1;
                        return 100 * n;
                    }
                default: return 1;
            }
        }

        protected virtual long GetPriceForSysCall()
        {
            if (CurrentContext.InstructionPointer >= CurrentContext.Script.Length - 3)
                return 1;
            byte length = CurrentContext.Script[CurrentContext.InstructionPointer + 1];
            if (CurrentContext.InstructionPointer > CurrentContext.Script.Length - length - 2)
                return 1;
            string api_name = Encoding.ASCII.GetString(CurrentContext.Script, CurrentContext.InstructionPointer + 2, length);
            switch (api_name)
            {
                case "Neo.Runtime.CheckWitness":
                case "AntShares.Runtime.CheckWitness":
                    return 200;
                case "Neo.Blockchain.GetHeader":
                case "AntShares.Blockchain.GetHeader":
                    return 100;
                case "Neo.Blockchain.GetBlock":
                case "AntShares.Blockchain.GetBlock":
                    return 200;
                case "Neo.Blockchain.GetTransaction":
                case "AntShares.Blockchain.GetTransaction":
                    return 100;
                case "Neo.Blockchain.GetAccount":
                case "AntShares.Blockchain.GetAccount":
                    return 100;
                case "Neo.Blockchain.GetValidators":
                case "AntShares.Blockchain.GetValidators":
                    return 200;
                case "Neo.Blockchain.GetAsset":
                case "AntShares.Blockchain.GetAsset":
                    return 100;
                case "Neo.Blockchain.GetContract":
                case "AntShares.Blockchain.GetContract":
                    return 100;
                case "Neo.Transaction.GetReferences":
                case "AntShares.Transaction.GetReferences":
                case "Neo.Transaction.GetUnspentCoins":
                    return 200;
                case "Neo.Account.SetVotes":
                case "AntShares.Account.SetVotes":
                    return 1000;
                case "Neo.Validator.Register":
                case "AntShares.Validator.Register":
                    return 1000L * 100000000L / ratio;
                case "Neo.Asset.Create":
                case "AntShares.Asset.Create":
                    return 5000L * 100000000L / ratio;
                case "Neo.Asset.Renew":
                case "AntShares.Asset.Renew":
                    return (byte)EvaluationStack.Peek(1).GetBigInteger() * 5000L * 100000000L / ratio;
                case "Neo.Contract.Create":
                case "Neo.Contract.Migrate":
                case "AntShares.Contract.Create":
                case "AntShares.Contract.Migrate":
                    long fee = 100L;

                    ContractPropertyState contract_properties = (ContractPropertyState)(byte)EvaluationStack.Peek(3).GetBigInteger();

                    if (contract_properties.HasFlag(ContractPropertyState.HasStorage))
                    {
                        fee += 400L;
                    }
                    if (contract_properties.HasFlag(ContractPropertyState.HasDynamicInvoke))
                    {
                        fee += 500L;
                    }
                    return fee * 100000000L / ratio;
                case "Neo.Storage.Get":
                case "AntShares.Storage.Get":
                    return 100;
                case "Neo.Storage.Put":
                case "AntShares.Storage.Put":
                    return ((EvaluationStack.Peek(1).GetByteArray().Length + EvaluationStack.Peek(2).GetByteArray().Length - 1) / 1024 + 1) * 1000;
                case "Neo.Storage.Delete":
                case "AntShares.Storage.Delete":
                    return 100;
                default:
                    return 1;
            }
        }

        public static ApplicationEngine RunWithDebug(byte[] script, IScriptContainer container = null, Block persisting_block = null)
        {
            if (persisting_block == null)
                persisting_block = new Block
                {
                    Version = 0,
                    PrevHash = Blockchain.Default.CurrentBlockHash,
                    MerkleRoot = new UInt256(),
                    Timestamp = Blockchain.Default.GetHeader(Blockchain.Default.Height).Timestamp + Blockchain.SecondsPerBlock,
                    Index = Blockchain.Default.Height + 1,
                    ConsensusData = 0,
                    NextConsensus = Blockchain.Default.GetHeader(Blockchain.Default.Height).NextConsensus,
                    Script = new Witness
                    {
                        InvocationScript = new byte[0],
                        VerificationScript = new byte[0]
                    },
                    Transactions = new Transaction[0]
                };
            DataCache<UInt160, AccountState> accounts = Blockchain.Default.CreateCache<UInt160, AccountState>();
            DataCache<ECPoint, ValidatorState> validators = Blockchain.Default.CreateCache<ECPoint, ValidatorState>();
            DataCache<UInt256, AssetState> assets = Blockchain.Default.CreateCache<UInt256, AssetState>();
            DataCache<UInt160, ContractState> contracts = Blockchain.Default.CreateCache<UInt160, ContractState>();
            DataCache<StorageKey, StorageItem> storages = Blockchain.Default.CreateCache<StorageKey, StorageItem>();
            CachedScriptTable script_table = new CachedScriptTable(contracts);
            StateMachine service = new StateMachine(persisting_block, accounts, validators, assets, contracts, storages);
            ApplicationEngine engine = new ApplicationEngine(TriggerType.Application, container, script_table, service, Fixed8.Zero, true);
            engine.BeginDebug();
            engine.LoadScript(script, false);
            engine.Execute();
            return engine;
        }
		
		
        public static ApplicationEngine Run(byte[] script, IScriptContainer container = null, Block persisting_block = null)
        {
            if (persisting_block == null)
                persisting_block = new Block
                {
                    Version = 0,
                    PrevHash = Blockchain.Default.CurrentBlockHash,
                    MerkleRoot = new UInt256(),
                    Timestamp = Blockchain.Default.GetHeader(Blockchain.Default.Height).Timestamp + Blockchain.SecondsPerBlock,
                    Index = Blockchain.Default.Height + 1,
                    ConsensusData = 0,
                    NextConsensus = Blockchain.Default.GetHeader(Blockchain.Default.Height).NextConsensus,
                    Script = new Witness
                    {
                        InvocationScript = new byte[0],
                        VerificationScript = new byte[0]
                    },
                    Transactions = new Transaction[0]
                };
            DataCache<UInt160, AccountState> accounts = Blockchain.Default.CreateCache<UInt160, AccountState>();
            DataCache<ECPoint, ValidatorState> validators = Blockchain.Default.CreateCache<ECPoint, ValidatorState>();
            DataCache<UInt256, AssetState> assets = Blockchain.Default.CreateCache<UInt256, AssetState>();
            DataCache<UInt160, ContractState> contracts = Blockchain.Default.CreateCache<UInt160, ContractState>();
            DataCache<StorageKey, StorageItem> storages = Blockchain.Default.CreateCache<StorageKey, StorageItem>();
            CachedScriptTable script_table = new CachedScriptTable(contracts);
            StateMachine service = new StateMachine(persisting_block, accounts, validators, assets, contracts, storages);
            ApplicationEngine engine = new ApplicationEngine(TriggerType.Application, container, script_table, service, Fixed8.Zero, true);
            engine.LoadScript(script, false);
            engine.Execute();
            return engine;
        }

        public Neo.SmartContract.Debug.FullLog FullLog
        {
            get;
            private set;
        }
        public void BeginDebug()
        {//打开Log
            this.FullLog = new FullLog();

            var sm = this.service as StateMachine;
            if (sm != null)
            {
                sm.BeginDebug(this.FullLog);
            }
        }

        public override void LoadScript(byte[] script, bool push_only = false)
        {
            if (this.FullLog != null)
            {
                var hash = script.ToScriptHash().ToString();
                this.FullLog.LoadScript(hash);
            }
            base.LoadScript(script, push_only);
        }
        void LogResult(VM.OpCode nextOpcode, VM.ExecutionStackRecord.Op[] records, VM.StackItem lastrecord)
        {
            if (records != null && records.Length > 0)
            {
                this.FullLog.OPStackRecord(records.ToArray());
            }
            if (lastrecord != null)
            {
                this.FullLog.OpResult(lastrecord);
            }
        }

    }
}
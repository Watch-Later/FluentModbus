using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentModbus
{
    internal abstract class ModbusRequestHandler : IDisposable
    {
        #region Fields

        private Task _task;

        #endregion

        #region Constructors

        public ModbusRequestHandler(ModbusServer modbusServer, int bufferSize)
        {
            this.ModbusServer = modbusServer;
            this.FrameBuffer = new ModbusFrameBuffer(bufferSize);

            this.LastRequest = Stopwatch.StartNew();
            this.IsReady = true;

            this.CTS = new CancellationTokenSource();
        }

        #endregion

        #region Properties

        public ModbusServer ModbusServer { get; }

        public Stopwatch LastRequest { get; protected set; }

        public int Length { get; protected set; }

        public bool IsReady { get; protected set; }

        public abstract string DisplayName { get; }

        protected byte UnitIdentifier { get; set; }

        protected CancellationTokenSource CTS { get; }

        protected ModbusFrameBuffer FrameBuffer { get; }

        protected abstract bool IsResponseRequired { get; }

        #endregion

        #region Methods

        public void WriteResponse()
        {
            int frameLength;
            Action processingMethod;

            if (!this.IsResponseRequired)
                return;

            if (!(this.IsReady && this.Length > 0))
                throw new Exception(ErrorMessage.ModbusTcpRequestHandler_NoValidRequestAvailable);

            var rawFunctionCode = this.FrameBuffer.Reader.ReadByte();                                              // 07     Function Code

            this.FrameBuffer.Writer.Seek(0, SeekOrigin.Begin);

            if (Enum.IsDefined(typeof(ModbusFunctionCode), rawFunctionCode))
            {
                var functionCode = (ModbusFunctionCode)rawFunctionCode;

                try
                {
                    processingMethod = functionCode switch
                    {
                        ModbusFunctionCode.ReadHoldingRegisters => this.ProcessReadHoldingRegisters,
                        ModbusFunctionCode.WriteMultipleRegisters => this.ProcessWriteMultipleRegisters,
                        ModbusFunctionCode.ReadCoils => this.ProcessReadCoils,
                        ModbusFunctionCode.ReadDiscreteInputs => this.ProcessReadDiscreteInputs,
                        ModbusFunctionCode.ReadInputRegisters => this.ProcessReadInputRegisters,
                        ModbusFunctionCode.WriteSingleCoil => this.ProcessWriteSingleCoil,
                        ModbusFunctionCode.WriteSingleRegister => this.ProcessWriteSingleRegister,
                        //ModbusFunctionCode.ReadExceptionStatus
                        //ModbusFunctionCode.WriteMultipleCoils
                        //ModbusFunctionCode.ReadFileRecord
                        //ModbusFunctionCode.WriteFileRecord
                        //ModbusFunctionCode.MaskWriteRegister
                        ModbusFunctionCode.ReadWriteMultipleRegisters => this.ProcessReadWriteMultipleRegisters,
                        //ModbusFunctionCode.ReadFifoQueue
                        //ModbusFunctionCode.Error
                        _ => () => this.WriteExceptionResponse(rawFunctionCode, ModbusExceptionCode.IllegalFunction)
                    };
                }
                catch (Exception)
                {
                    processingMethod = () => this.WriteExceptionResponse(rawFunctionCode, ModbusExceptionCode.ServerDeviceFailure);
                }
            }
            else
            {
                processingMethod = () => this.WriteExceptionResponse(rawFunctionCode, ModbusExceptionCode.IllegalFunction);
            }

            // if incoming frames shall be processed asynchronously, access to memory must be orchestrated
            if (this.ModbusServer.IsAsynchronous)
            {
                lock (this.ModbusServer.Lock)
                {
                    frameLength = this.WriteFrame(processingMethod);
                }
            }
            else
            {
                frameLength = this.WriteFrame(processingMethod);
            }

            this.OnResponseReady(frameLength);
        }

        internal abstract Task ReceiveRequestAsync();

        protected void Start()
        {
            if (this.ModbusServer.IsAsynchronous)
            {
                _task = Task.Run(async () =>
                {
                    while (!this.CTS.IsCancellationRequested)
                    {
                        await this.ReceiveRequestAsync();
                    }
                }, this.CTS.Token);
            }
        }

        protected abstract int WriteFrame(Action extendFrame);

        protected abstract void OnResponseReady(int frameLength);

        private void WriteExceptionResponse(ModbusFunctionCode functionCode, ModbusExceptionCode exceptionCode)
        {
            this.WriteExceptionResponse((byte)functionCode, exceptionCode);
        }

        private void WriteExceptionResponse(byte rawFunctionCode, ModbusExceptionCode exceptionCode)
        {
            this.FrameBuffer.Writer.Write((byte)(ModbusFunctionCode.Error + rawFunctionCode));
            this.FrameBuffer.Writer.Write((byte)exceptionCode);
        }

        private bool CheckRegisterBounds(ModbusFunctionCode functionCode, ushort address, ushort maxStartingAddress, ushort quantityOfRegisters, ushort maxQuantityOfRegisters)
        {
            if (this.ModbusServer.RequestValidator != null)
            {
                var result = this.ModbusServer.RequestValidator(this.UnitIdentifier, functionCode, address, quantityOfRegisters);

                if (result > ModbusExceptionCode.OK)
                {
                    this.WriteExceptionResponse(functionCode, result);
                    return false;
                }
            }

            if (address < 0 || address + quantityOfRegisters > maxStartingAddress)
            {
                this.WriteExceptionResponse(functionCode, ModbusExceptionCode.IllegalDataAddress);
                return false;
            }

            if (quantityOfRegisters <= 0 || quantityOfRegisters > maxQuantityOfRegisters)
            {
                this.WriteExceptionResponse(functionCode, ModbusExceptionCode.IllegalDataValue);
                return false;
            }

            return true;
        }

        private void DetectChangedRegisters(int startingAddress, Span<short> oldValues, Span<short> newValues)
        {
            Span<int> changedRegisters = stackalloc int[newValues.Length];

            var index = 0;

            for (int i = 0; i < newValues.Length; i++)
            {
                if (newValues[i] != oldValues[i])
                {
                    changedRegisters[index] = startingAddress + i;
                    index++;
                }
            }

            this.ModbusServer.OnRegistersChanged(this.UnitIdentifier, changedRegisters.Slice(0, index).ToArray());
        }

        // class 0
        private void ProcessReadHoldingRegisters()
        {
            var startingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityOfRegisters = this.FrameBuffer.Reader.ReadUInt16Reverse();

            if (this.CheckRegisterBounds(ModbusFunctionCode.ReadHoldingRegisters, startingAddress, this.ModbusServer.MaxHoldingRegisterAddress, quantityOfRegisters, 0x7D))
            {
                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.ReadHoldingRegisters);
                this.FrameBuffer.Writer.Write((byte)(quantityOfRegisters * 2));
                this.FrameBuffer.Writer.Write(this.ModbusServer.GetHoldingRegisterBuffer(this.UnitIdentifier).Slice(startingAddress * 2, quantityOfRegisters * 2).ToArray());
            }
        }

        private void ProcessWriteMultipleRegisters()
        {
            var startingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityOfRegisters = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var byteCount = this.FrameBuffer.Reader.ReadByte();

            if (this.CheckRegisterBounds(ModbusFunctionCode.WriteMultipleRegisters, startingAddress, this.ModbusServer.MaxHoldingRegisterAddress, quantityOfRegisters, 0x7B))
            {
                var holdingRegisters = this.ModbusServer.GetHoldingRegisters(this.UnitIdentifier);
                var oldValues = holdingRegisters.Slice(startingAddress).ToArray();
                var newValues = MemoryMarshal.Cast<byte, short>(this.FrameBuffer.Reader.ReadBytes(byteCount).AsSpan());

                newValues.CopyTo(holdingRegisters.Slice(startingAddress));

                if (this.ModbusServer.EnableRaisingEvents)
                    this.DetectChangedRegisters(startingAddress, oldValues, newValues);

                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.WriteMultipleRegisters);

                if (BitConverter.IsLittleEndian)
                {
                    this.FrameBuffer.Writer.WriteReverse(startingAddress);
                    this.FrameBuffer.Writer.WriteReverse(quantityOfRegisters);
                }
                else
                {
                    this.FrameBuffer.Writer.Write(startingAddress);
                    this.FrameBuffer.Writer.Write(quantityOfRegisters);
                }
            }
        }

        // class 1
        private void ProcessReadCoils()
        {
            var startingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityOfCoils = this.FrameBuffer.Reader.ReadUInt16Reverse();

            if (this.CheckRegisterBounds(ModbusFunctionCode.ReadCoils, startingAddress, this.ModbusServer.MaxCoilAddress, quantityOfCoils, 0x7D0))
            {
                var byteCount = (byte)Math.Ceiling((double)quantityOfCoils / 8);

                var coilBuffer = this.ModbusServer.GetCoilBuffer(this.UnitIdentifier);
                var targetBuffer = new byte[byteCount];

                for (int i = 0; i < quantityOfCoils; i++)
                {
                    var sourceByteIndex = (startingAddress + i) / 8;
                    var sourceBitIndex = (startingAddress + i) % 8;

                    var targetByteIndex = i / 8;
                    var targetBitIndex = i % 8;

                    var isSet = (coilBuffer[sourceByteIndex] & (1 << sourceBitIndex)) > 0;

                    if (isSet)
                        targetBuffer[targetByteIndex] |= (byte)(1 << targetBitIndex);
                }

                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.ReadCoils);
                this.FrameBuffer.Writer.Write(byteCount);
                this.FrameBuffer.Writer.Write(targetBuffer);
            }
        }

        private void ProcessReadDiscreteInputs()
        {
            var startingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityOfInputs = this.FrameBuffer.Reader.ReadUInt16Reverse();

            if (this.CheckRegisterBounds(ModbusFunctionCode.ReadDiscreteInputs, startingAddress, this.ModbusServer.MaxInputRegisterAddress, quantityOfInputs, 0x7D0))
            {
                var byteCount = (byte)Math.Ceiling((double)quantityOfInputs / 8);

                var discreteInputBuffer = this.ModbusServer.GetDiscreteInputBuffer(this.UnitIdentifier);
                var targetBuffer = new byte[byteCount];

                for (int i = 0; i < quantityOfInputs; i++)
                {
                    var sourceByteIndex = (startingAddress + i) / 8;
                    var sourceBitIndex = (startingAddress + i) % 8;

                    var targetByteIndex = i / 8;
                    var targetBitIndex = i % 8;

                    var isSet = (discreteInputBuffer[sourceByteIndex] & (1 << sourceBitIndex)) > 0;

                    if (isSet)
                        targetBuffer[targetByteIndex] |= (byte)(1 << targetBitIndex);
                }

                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.ReadDiscreteInputs);
                this.FrameBuffer.Writer.Write(byteCount);
                this.FrameBuffer.Writer.Write(targetBuffer);
            }
        }

        private void ProcessReadInputRegisters()
        {
            var startingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityOfRegisters = this.FrameBuffer.Reader.ReadUInt16Reverse();

            if (this.CheckRegisterBounds(ModbusFunctionCode.ReadInputRegisters, startingAddress, this.ModbusServer.MaxInputRegisterAddress, quantityOfRegisters, 0x7D))
            {
                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.ReadInputRegisters);
                this.FrameBuffer.Writer.Write((byte)(quantityOfRegisters * 2));
                this.FrameBuffer.Writer.Write(this.ModbusServer.GetInputRegisterBuffer(this.UnitIdentifier).Slice(startingAddress * 2, quantityOfRegisters * 2).ToArray());
            }
        }

        private void ProcessWriteSingleCoil()
        {
            var outputAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var outputValue = this.FrameBuffer.Reader.ReadUInt16();

            if (this.CheckRegisterBounds(ModbusFunctionCode.WriteSingleCoil, outputAddress, this.ModbusServer.MaxCoilAddress, 1, 1))
            {
                if (outputValue != 0x0000 && outputValue != 0x00FF)
                {
                    this.WriteExceptionResponse(ModbusFunctionCode.ReadHoldingRegisters, ModbusExceptionCode.IllegalDataValue);
                }
                else
                {
                    var bufferByteIndex = outputAddress / 8;
                    var bufferBitIndex = outputAddress % 8;

                    var coils = this.ModbusServer.GetCoils(this.UnitIdentifier);
                    var oldValue = coils[bufferByteIndex];
                    var newValue = oldValue;

                    if (outputValue == 0x0000)
                        newValue &= (byte)~(1 << bufferBitIndex);
                    else
                        newValue |= (byte)(1 << bufferBitIndex);

                    coils[bufferByteIndex] = newValue;

                    if (this.ModbusServer.EnableRaisingEvents && newValue != oldValue)
                        this.ModbusServer.OnCoilsChanged(this.UnitIdentifier, new int[] { outputAddress });

                    this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.WriteSingleCoil);

                    if (BitConverter.IsLittleEndian)
                        this.FrameBuffer.Writer.WriteReverse(outputAddress);
                    else
                        this.FrameBuffer.Writer.Write(outputAddress);

                    this.FrameBuffer.Writer.Write(outputValue);
                }
            }
        }

        private void ProcessWriteSingleRegister()
        {
            var registerAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var registerValue = this.FrameBuffer.Reader.ReadInt16();

            if (this.CheckRegisterBounds(ModbusFunctionCode.WriteSingleRegister, registerAddress, this.ModbusServer.MaxHoldingRegisterAddress, 1, 1))
            {
                var holdingRegisters = this.ModbusServer.GetHoldingRegisters(this.UnitIdentifier);
                var oldValue = holdingRegisters[registerAddress];
                var newValue = registerValue;
                holdingRegisters[registerAddress] = newValue;

                if (this.ModbusServer.EnableRaisingEvents && newValue != oldValue)
                    this.ModbusServer.OnRegistersChanged(this.UnitIdentifier, new int[] { registerAddress });

                this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.WriteSingleRegister);

                if (BitConverter.IsLittleEndian)
                    this.FrameBuffer.Writer.WriteReverse(registerAddress);
                else
                    this.FrameBuffer.Writer.Write(registerAddress);

                this.FrameBuffer.Writer.Write(registerValue);
            }
        }

        // class 2
        private void ProcessReadWriteMultipleRegisters()
        {
            var readStartingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityToRead = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var writeStartingAddress = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var quantityToWrite = this.FrameBuffer.Reader.ReadUInt16Reverse();
            var writeByteCount = this.FrameBuffer.Reader.ReadByte();

            if (this.CheckRegisterBounds(ModbusFunctionCode.ReadWriteMultipleRegisters, readStartingAddress, this.ModbusServer.MaxHoldingRegisterAddress, quantityToRead, 0x7D))
            {
                if (this.CheckRegisterBounds(ModbusFunctionCode.ReadWriteMultipleRegisters, writeStartingAddress, this.ModbusServer.MaxHoldingRegisterAddress, quantityToWrite, 0x7B))
                {
                    var holdingRegisters = this.ModbusServer.GetHoldingRegisters(this.UnitIdentifier);

                    // write data (write is performed before read according to spec)
                    var writeData = MemoryMarshal.Cast<byte, short>(this.FrameBuffer.Reader.ReadBytes(writeByteCount).AsSpan());

                    var oldValues = holdingRegisters.Slice(writeStartingAddress).ToArray();
                    var newValues = writeData;

                    newValues.CopyTo(holdingRegisters.Slice(writeStartingAddress));

                    if (this.ModbusServer.EnableRaisingEvents)
                        this.DetectChangedRegisters(writeStartingAddress, oldValues, newValues);

                    // read data
                    var readData = MemoryMarshal.AsBytes(holdingRegisters
                        .Slice(readStartingAddress, quantityToRead))
                        .ToArray();

                    // write response
                    this.FrameBuffer.Writer.Write((byte)ModbusFunctionCode.ReadWriteMultipleRegisters);
                    this.FrameBuffer.Writer.Write((byte)(quantityToRead * 2));
                    this.FrameBuffer.Writer.Write(readData);
                }
            }
        }

        #endregion

        #region IDisposable Support

        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (this.ModbusServer.IsAsynchronous)
                    {
                        this.CTS?.Cancel();

                        try
                        {
                            _task?.Wait();
                        }
                        catch (Exception ex) when (ex.InnerException.GetType() == typeof(TaskCanceledException))
                        {
                            // Actually, TaskCanceledException is not expected because it is catched in ReceiveRequestAsync() method.
                        }
                    }

                    this.FrameBuffer.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}

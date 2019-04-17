﻿#define SPIDERVERSE_DEBUG

using System;
using System.Net.Sockets;
using ControlRoomApplication.Constants;
using ControlRoomApplication.Entities;
using Modbus.Device;

namespace ControlRoomApplication.Controllers
{
    public class IntoTheSpiderversePLCDriver : AbstractPLCDriver
    {
        private static readonly log4net.ILog logger = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private TcpClient MCUTCPClient;
        private ModbusIpMaster MCUModbusMaster;

        public IntoTheSpiderversePLCDriver(string ipLocal, int portLocal) : base(ipLocal, portLocal)
        {
            MCUTCPClient = new TcpClient(MCUConstants.ACTUAL_MCU_IP_ADDRESS, MCUConstants.ACTUAL_MCU_MODBUS_TCP_PORT);
            MCUModbusMaster = ModbusIpMaster.CreateIp(MCUTCPClient);
        }

        ~IntoTheSpiderversePLCDriver()
        {
            if (MCUTCPClient != null)
            {
                MCUTCPClient.Close();
            }
        }

        //public void FetchMCUModbusSlave(string ipMCU, int portMCU)
        //{
        //    MCUTCPClient = new TcpClient(ipMCU, portMCU);
        //    MCUModbusMaster = ModbusIpMaster.CreateIp(MCUTCPClient);
        //}

        public void PrintReadInputRegsiterContents(string header)
        {
#if SPIDERVERSE_DEBUG
            System.Threading.Thread.Sleep(50);

            ushort[] inputRegisters = MCUModbusMaster.ReadInputRegisters(MCUConstants.ACTUAL_MCU_READ_INPUT_REGISTER_START_ADDRESS, 10);
            Console.WriteLine(header + ":");
            foreach (ushort us in inputRegisters)
            {
                string usString = Convert.ToString(us, 2);
                usString = new string('0', 16 - usString.Length) + usString;
                usString = usString.Insert(4, " ");
                usString = usString.Insert(9, " ");
                usString = usString.Insert(14, " ");

                Console.WriteLine('\t'.ToString() + usString);
            }
#endif
        }

        public bool SendResetErrorsCommand()
        {
            PrintReadInputRegsiterContents("Before reset");

            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, new ushort[] { 0x0800, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0 });

            PrintReadInputRegsiterContents("After reset");

            return true;
        }

        public bool SendHoldMoveCommand()
        {
            PrintReadInputRegsiterContents("Before controlled stop");

            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, new ushort[] { 0x4, 0x3, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0 });

            PrintReadInputRegsiterContents("After controlled stop");

            return true;
        }

        public bool SendImmediateStopCommand()
        {
            PrintReadInputRegsiterContents("Before controlled stop");

            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, new ushort[] { 0x10, 0x3, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0 });

            PrintReadInputRegsiterContents("After controlled stop");

            return true;
        }

        public bool SendEmptyMoveCommand()
        {
            PrintReadInputRegsiterContents("Before removing move bits");

            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, new ushort[] { 0x0, 0x3, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0 });

            PrintReadInputRegsiterContents("After removing move bits");

            return true;
        }

        protected override bool ProcessRequest(NetworkStream ActiveClientStream, byte[] query)
        {
            int ExpectedSize = query[0] + (256 * query[1]);
            if (query.Length != ExpectedSize)
            {
                throw new ArgumentException(
                    "IntoTheSpiderversePLCDriver read a package specifying a size [" + ExpectedSize.ToString() + "], but the actual size was different [" + query.Length + "]."
                );
            }

            byte CommandQueryTypeAndExpectedResponseStatus = query[2];
            byte CommandQueryTypeByte = (byte)(CommandQueryTypeAndExpectedResponseStatus & 0x3F);
            byte ExpectedResponseStatusByte = (byte)(CommandQueryTypeAndExpectedResponseStatus >> 6);

            PLCCommandAndQueryTypeEnum CommandQueryTypeEnum = PLCCommandAndQueryTypeConversionHelper.GetFromByte(CommandQueryTypeByte);
            PLCCommandResponseExpectationEnum ExpectedResponseStatusEnum = PLCCommandResponseExpectationConversionHelper.GetFromByte(ExpectedResponseStatusByte);

            byte[] FinalResponseContainer;

            if (ExpectedResponseStatusEnum == PLCCommandResponseExpectationEnum.FULL_RESPONSE)
            {
                FinalResponseContainer = new byte[]
                {
                    0x13, 0x0,
                    0x0,
                    0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
                    0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0
                };

                switch (CommandQueryTypeEnum)
                {
                    case PLCCommandAndQueryTypeEnum.TEST_CONNECTION:
                        {
                            // Read the heartbeat register
                            ushort[] inputRegisters = MCUModbusMaster.ReadInputRegisters(MCUConstants.ACTUAL_MCU_READ_INPUT_REGISTER_HEARTBEAT_ADDRESS, 1);
                            ushort resultValue = (ushort)((inputRegisters.Length == 1) ? inputRegisters[0] : 0);
                            FinalResponseContainer[3] = (byte)(((resultValue == 8192) || (resultValue == 24576)) ? 0x1 : 0x0);

                            FinalResponseContainer[2] = 0x1;
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.GET_CURRENT_AZEL_POSITIONS:
                        {
                            PrintReadInputRegsiterContents("Before getting current position");

                            // Get the MCU's value for the displacement since its power cycle
                            ushort[] inputRegisters = MCUModbusMaster.ReadInputRegisters(MCUConstants.ACTUAL_MCU_READ_INPUT_REGISTER_CURRENT_POSITION_ADDRESS, 2);
                            int currentStepForMCU = (65536 * inputRegisters[0]) + inputRegisters[1];

                            PrintReadInputRegsiterContents("After getting current position");

                            // Convert that step change into degrees and write the bytes to return
                            Array.Copy(BitConverter.GetBytes(currentStepForMCU * 360 / 10000000.0), 0, FinalResponseContainer, 3, 8);
                            Array.Copy(BitConverter.GetBytes(0.0), 0, FinalResponseContainer, 11, 8);

                            FinalResponseContainer[2] = 0x1;
                            break;
                        }

                    default:
                        {
                            throw new ArgumentException("Invalid PLCCommandAndQueryTypeEnum value seen while expecting a response: " + CommandQueryTypeEnum.ToString());
                        }
                }
            }
            else if (ExpectedResponseStatusEnum == PLCCommandResponseExpectationEnum.MINOR_RESPONSE)
            {
                FinalResponseContainer = new byte[]
                {
                    0x3, 0x0, 0x0
                };

                switch (CommandQueryTypeEnum)
                {
                    case PLCCommandAndQueryTypeEnum.SET_CONFIGURATION:
                        {
                            // Copy over data we care about, which for now is only the azimuth
                            // We skip over the data concerning the elevation, hence the gap in element access for query
                            ushort[] DataToWrite =
                            {
                                (ushort)((256 * query[3]) + query[4]),
                                (ushort)((256 * query[5]) + query[6]),
                                (ushort)((256 * query[7]) + query[8]),
                                (ushort)((256 * query[9]) + query[10]),
                                (ushort)((256 * query[15]) + query[16]),
                                0,
                                0,
                                0,
                                0,
                                0
                            };

                            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, DataToWrite);
                            
                            PrintReadInputRegsiterContents("After setting configuration");
                            if (SendResetErrorsCommand())
                            {
                                Console.WriteLine("[IntoTheSpiderversePLCDriver] Successfully sent reset command.");
                                PrintReadInputRegsiterContents("After sending reset command");
                                FinalResponseContainer[2] = 0x1;
                            }
                            else
                            {
                                // Send an error code
                                Console.WriteLine("[IntoTheSpiderversePLCDriver] ERROR sending reset command.");
                                FinalResponseContainer[2] = 0x2;
                            }
                            
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.CONTROLLED_STOP:
                        {
                            // There was already a helper function to execute a controlled stop, so just call that
                            // Send an error code if there's a failure for some reason
                            FinalResponseContainer[2] = (byte)(SendEmptyMoveCommand() ? 0x1 : 0x2);
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.IMMEDIATE_STOP:
                        {
                            // There was already a helper function to execute a controlled stop, so just call that
                            // Send an error code if there's a failure for some reason
                            FinalResponseContainer[2] = (byte)(SendImmediateStopCommand() ? 0x1 : 0x2);
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.SET_OBJECTIVE_AZEL_POSITION:
                        {
                            PrintReadInputRegsiterContents("Before setting objective position");

                            // Copy over data we care about, so skip over the data concerning the elevation
                            double discrepancyMultiplier = 1.0;
                            double objectiveAZDouble = BitConverter.ToDouble(query, 3);
                            int stepChange = (int)(discrepancyMultiplier * Math.Pow(2, MCUConstants.ACTUAL_MCU_AZIMUTH_ENCODER_BIT_RESOLUTION) * objectiveAZDouble / 360);
                            ushort stepChangeUShortMSW = (ushort)((stepChange >> 16) & 0xFFFF);
                            ushort stepChangeUShortLSW = (ushort)(stepChange & 0xFFFF);

                            int programmedPeakSpeed = BitConverter.ToInt32(query, 7);
                            ushort programmedPeakSpeedUShortMSW = (ushort)((programmedPeakSpeed >> 16) & 0xFFFF);
                            ushort programmedPeakSpeedUShortLSW = (ushort)(programmedPeakSpeed & 0xFFFF);

                            ushort[] DataToWrite =
                            {
                                0x2, // Denotes a relative move in command mode
                                0x3, // Denotes a Trapezoidal S-Curve profile
                                stepChangeUShortMSW,
                                stepChangeUShortLSW,
                                programmedPeakSpeedUShortMSW,
                                programmedPeakSpeedUShortLSW,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                0,
                                0
                            };

                            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, DataToWrite);

                            PrintReadInputRegsiterContents("After setting objective position");

                            FinalResponseContainer[2] = 0x1;
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.START_JOG_MOVEMENT:
                        {
                            PrintReadInputRegsiterContents("Before starting jog command");

                            // Make sure the command is intended for the azimuth
                            if (query[3] != 0x1)
                            {
                                throw new ArgumentException("Unsupported value for axis specified in jog command for IntoTheSpiderversePLCDriver: " + query[3].ToString());
                            }

                            ushort programmedPeakSpeedUShortMSW = (ushort)((256 * query[4]) + query[5]);
                            ushort programmedPeakSpeedUShortLSW = (ushort)((256 * query[6]) + query[7]);

                            ushort commandCode;
                            switch (query[8])
                            {
                                case 0x1:
                                    {
                                        commandCode = 0x80;
                                        break;
                                    }
                                
                                case 0x2:
                                    {
                                        commandCode = 0x100;
                                        break;
                                    }

                                default:
                                    {
                                        throw new ArgumentException("Unsupported value for motor movement direction in jog command for IntoTheSpiderversePLCDriver: " + query[8].ToString());
                                    }
                            }

                            ushort[] DataToWrite =
                            {
                                commandCode,  // Denotes a jog move, either CW or CCW, in command mode
                                0x3,          // Denotes a Trapezoidal S-Curve profile
                                0,            // Reserved to 0 for a jog command
                                0,            // Reserved to 0 for a jog command
                                programmedPeakSpeedUShortMSW,
                                programmedPeakSpeedUShortLSW,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                0,
                                0
                            };

                            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, DataToWrite);
                            
                            PrintReadInputRegsiterContents("After starting jog command");

                            FinalResponseContainer[2] = 0x1;
                            break;
                        }

                    case PLCCommandAndQueryTypeEnum.START_RELATIVE_MOVE:
                        {
                            PrintReadInputRegsiterContents("Before starting relative move");

                            // Make sure the command is intended for the azimuth
                            if (query[3] != 0x1)
                            {
                                throw new ArgumentException("Unsupported value for axis specified in move relative command for IntoTheSpiderversePLCDriver: " + query[3].ToString());
                            }

                            ushort programmedPeakSpeedUShortMSW = (ushort)((256 * query[4]) + query[5]);
                            ushort programmedPeakSpeedUShortLSW = (ushort)((256 * query[6]) + query[7]);

                            short programmedPositionUShortMSW = (short)((256 * query[8]) + query[9]);
                            short programmedPositionUShortLSW = (short)((256 * query[10]) + query[11]);

                            ushort commandCode = 0x2;
                            

                            ushort[] DataToWrite =
                            {
                                commandCode,  // Denotes a relative move
                                0x3,          // Denotes a Trapezoidal S-Curve profile
                                (ushort)programmedPositionUShortMSW,            // MSW for Position
                                (ushort)programmedPositionUShortLSW,        // LSW for Position
                                programmedPeakSpeedUShortMSW,
                                programmedPeakSpeedUShortLSW,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_SPIDERVERSE,
                                0,
                                0
                            };

                            MCUModbusMaster.WriteMultipleRegisters(MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS, DataToWrite);

                            PrintReadInputRegsiterContents("After starting relative move command");

                            FinalResponseContainer[2] = 0x1;
                            break;
                        }

                    default:
                        {
                            throw new ArgumentException("Invalid PLCCommandAndQueryTypeEnum value seen while NOT expecting a response: " + CommandQueryTypeEnum.ToString());
                        }
                }
            }
            else
            {
                throw new ArgumentException("Invalid PLCCommandResponseExpectationEnum value seen while processing client request in IntoTheSpiderversePLCDriver: " + ExpectedResponseStatusEnum.ToString());
            }

            return AttemptToWriteDataToServer(ActiveClientStream, FinalResponseContainer);
        }
    }
}
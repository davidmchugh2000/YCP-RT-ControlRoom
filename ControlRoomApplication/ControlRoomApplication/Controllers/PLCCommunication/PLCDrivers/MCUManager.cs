﻿using ControlRoomApplication.Constants;
using ControlRoomApplication.Entities;
using ControlRoomApplication.Entities.Configuration;
using Modbus.Device;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ControlRoomApplication.Controllers {
    /// <summary>
    /// used to keep track of what comand the MCU is running
    /// </summary>
    public class MCUcomand {
        /// <summary>
        /// stores the data that is to be sent to the mcu
        /// </summary>
        public ushort[] comandData;
        /// <summary>
        /// high level information about the comands general purpose
        /// </summary>
        public MCUcomandType ComandType;
        /// <summary>
        /// time at which the comand was sent to the MCU
        /// </summary>
        public DateTime issued;
        /// <summary>
        /// true when comand has completed, used to determine when the next move can be sent
        /// </summary>
        public bool completed = false;
        /// <summary>
        /// this will be set when returnd to the calling function if the move could not be run for some reason
        /// </summary>
        public Exception ComandError;
        /// <summary>
        /// these variables set so that different parts of the MCUManager can calculate how parts of the operation will take
        /// </summary>
        public int AZ_Programed_Speed,EL_Programed_Speed,AZ_ACC,EL_ACC;

        /// <summary>
        /// create a MCU command and record the current time
        /// </summary>
        /// <param name="data"></param>
        /// <param name="CMDType"></param>
        public MCUcomand( ushort[] data, MCUcomandType CMDType ) {
            ComandType = CMDType;
            comandData = data;
            issued = DateTime.Now;
        }

    }
    public enum MCUcomandType {
        JOG,
        RELETIVE_MOVE,
        CONFIGURE,
        CLEAR_LAST_MOVE,
        HOLD_MOVE,
        IMIDEAT_STOP,
        RESET_ERRORS,
        HOME
    }

    public class MCUpositonRegs : MCUpositonStore {
        private ModbusIpMaster MCUModbusMaster;
        public MCUpositonRegs( ModbusIpMaster _MCUModbusMaster ):base() {
            MCUModbusMaster = _MCUModbusMaster;
        }
        public async Task update() {
            ushort[] data = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 20 ).GetAwaiter().GetResult();
            AZ_Steps = (data[(ushort)MCUConstants.MCUOutputRegs.AZ_Current_Position_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.AZ_Current_Position_LSW];
            EL_Steps = (data[(ushort)MCUConstants.MCUOutputRegs.EL_Current_Position_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.EL_Current_Position_LSW];
            AZ_Encoder = (data[(ushort)MCUConstants.MCUOutputRegs.AZ_MTR_Encoder_Pos_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.AZ_MTR_Encoder_Pos_LSW];
            EL_Encoder = (data[(ushort)MCUConstants.MCUOutputRegs.EL_MTR_Encoder_Pos_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.EL_MTR_Encoder_Pos_LSW];
            return;
        }
        public async Task<MCUpositonStore> updateAndReturnDif( MCUpositonStore previous ) {
            ushort[] data = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 20 ).GetAwaiter().GetResult();
            AZ_Steps = (data[(ushort)MCUConstants.MCUOutputRegs.AZ_Current_Position_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.AZ_Current_Position_LSW];
            EL_Steps = (data[(ushort)MCUConstants.MCUOutputRegs.EL_Current_Position_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.EL_Current_Position_LSW];
            AZ_Encoder = (data[(ushort)MCUConstants.MCUOutputRegs.AZ_MTR_Encoder_Pos_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.AZ_MTR_Encoder_Pos_LSW];
            EL_Encoder = (data[(ushort)MCUConstants.MCUOutputRegs.EL_MTR_Encoder_Pos_MSW] << 16) + data[(ushort)MCUConstants.MCUOutputRegs.EL_MTR_Encoder_Pos_LSW];
            MCUpositonStore dif = new MCUpositonStore( (this as MCUpositonStore) , previous);
            return dif;
        }
    }
    public class MCUpositonStore {
        public int AZ_Steps, EL_Steps;
        public int AZ_Encoder, EL_Encoder;
        public MCUpositonStore( ) {

        }
        public MCUpositonStore( MCUpositonStore current , MCUpositonStore previous ) {
            AZ_Steps = previous.AZ_Steps - current.AZ_Steps;
            EL_Steps = previous.EL_Steps - current.EL_Steps;
            AZ_Encoder = previous.AZ_Encoder - current.AZ_Encoder;
            EL_Encoder = previous.EL_Encoder - current.EL_Encoder;
        }

    }

    public class MCUManager {
        private static readonly log4net.ILog logger = log4net.LogManager.GetLogger( System.Reflection.MethodBase.GetCurrentMethod().DeclaringType );
        /// <summary>
        /// if more errors than this value are thrown in a row and this class cant resolve them subsiquent attempts to send moves to the MCU will throw exception
        /// </summary>
        private static int MaxConscErrors = 5;
        private static readonly ushort[] MESSAGE_CONTENTS_IMMEDIATE_STOP = new ushort[] {
            0x0010, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0010, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000
        };

        private static readonly ushort[] MESSAGE_CONTENTS_HOLD_MOVE = new ushort[] {
            0x0004, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0004, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000
        };

        private static readonly ushort[] MESSAGE_CONTENTS_CLEAR_MOVE = new ushort[] {
            0x0000, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0000, 0x0003, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000
        };

        private static readonly ushort[] MESSAGE_CONTENTS_RESET_ERRORS = new ushort[] {
            0x0800, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0800, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000
        };
        private long MCU_last_contact = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private Thread MCU_Monitor_Thread;
        private int AZStartSpeed = 0;
        private int ELStartSpeed = 0;
        private bool keep_modbus_server_alive = true;
        public ModbusIpMaster MCUModbusMaster;
        private TcpClient MCUTCPClient;
        public MCUpositonRegs mCUpositon;
        private MCUConfigurationAxys Current_AZConfiguration;
        private MCUConfigurationAxys Current_ELConfiguration;
        private MCUcomand RunningCommand= new MCUcomand(new ushort[20],MCUcomandType.CLEAR_LAST_MOVE);
        private int consecutiveErrors = 0;
        private int consecutiveSucsefullMoves = 0;

        public MCUManager( string MCU_ip , int MCU_port ) {

            try {
                MCUTCPClient = new TcpClient( MCU_ip , MCU_port );
                MCUModbusMaster = ModbusIpMaster.CreateIp( MCUTCPClient );
                mCUpositon = new MCUpositonRegs(MCUModbusMaster);
                MCU_Monitor_Thread = new Thread( new ThreadStart( MonitorMCU ) );
            } catch(Exception e) {
                if((e is ArgumentNullException) || (e is ArgumentOutOfRangeException)) {
                    logger.Info( "[AbstractPLCDriver] ERROR: failure creating PLC TCP server or management thread: " + e.ToString() );
                    return;
                } else { throw e; }// Unexpected exception
            }
        }

        private void MonitorMCU() {
            int lastMCUHeartbeatBit = 0;
            while(keep_modbus_server_alive) {
                ushort network_status = MCUModbusMaster.ReadHoldingRegisters( (ushort)MCUConstants.MCUOutputRegs.Network_Conectivity , 1 )[0];
                int CurrentHeartBeat = (network_status >> 14) & 1;//this bit changes every 500ms
                if(CurrentHeartBeat != lastMCUHeartbeatBit) {
                    MCU_last_contact = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                lastMCUHeartbeatBit = CurrentHeartBeat;
                if(((network_status >> 13) & 1) == 1) {
                    logger.Warn( "MCU network disconected, reseting errors" );
                    MCUModbusMaster.WriteMultipleRegistersAsync( MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS , MESSAGE_CONTENTS_RESET_ERRORS ).Wait();

                }
                Task.Delay( 250 ).Wait();
            }
        }

        public bool StartAsyncAcceptingClients() {
            keep_modbus_server_alive = true;
            try {
                MCU_Monitor_Thread.Start();
            } catch(Exception e) {
                if((e is ThreadStateException) || (e is OutOfMemoryException)) {
                    logger.Error( "failed to start prodi=uction plc and mcu threads err:____    {0}" , e );
                    return false;
                } else { throw e; }// Unexpected exception
            }
            return true;
        }


        public bool RequestStopAsyncAcceptingClientsAndJoin() {
            keep_modbus_server_alive = false;
            try {
                MCU_Monitor_Thread.Join();
            } catch(Exception e) {
                if((e is ThreadStateException) || (e is ThreadStartException)) {
                    logger.Error( e );
                    return false;
                } else { throw e; }// Unexpected exception
            }
            return true;
        }

        public ushort[] readModbusReregs( ushort startadress , ushort length ) {
            return MCUModbusMaster.ReadHoldingRegistersAsync( startadress , length ).GetAwaiter().GetResult();
        }

        public Orientation read_Position() {
            mCUpositon.update().Wait();
            return new Orientation(
                ConversionHelper.StepsToDegrees(mCUpositon.AZ_Steps, MotorConstants.GEARING_RATIO_AZIMUTH),
                ConversionHelper.StepsToDegrees(mCUpositon.EL_Steps, MotorConstants.GEARING_RATIO_ELEVATION)
            );
        }




        /// <summary>
        /// clears the previos move comand from mthe PLC, only works for jog moves
        /// </summary>
        /// <returns></returns>
        public bool Cancel_move() {
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_CLEAR_MOVE , MCUcomandType.CLEAR_LAST_MOVE ) ).GetAwaiter().GetResult();
            return true;
        }

        /// <summary>
        /// TODO: once i am trackin what the active move is this should check what is currently running a jog perform a clear instead of hold
        /// </summary>
        /// <returns></returns>
        public bool Controled_stop() {
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_HOLD_MOVE , MCUcomandType.HOLD_MOVE ) ).GetAwaiter().GetResult();
            return true;
        }

        public bool Immediade_stop() {
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_IMMEDIATE_STOP , MCUcomandType.IMIDEAT_STOP ) ).GetAwaiter().GetResult();
            return true;
        }


        private async Task<bool> Override_And_Stop_Motion() {
            var data = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 12 ).GetAwaiter().GetResult();
            if(Is_Moing( data )) {
                if(RunningCommand.ComandType == MCUcomandType.JOG) {
                    Cancel_move();
                    await WatTillStopped();
                }else if(RunningCommand.ComandType == MCUcomandType.RELETIVE_MOVE) {
                    Cancel_move();
                    Task.Delay( 100 ).Wait();
                    Controled_stop();
                    await WatTillStopped();
                } else {
                    Immediade_stop();
                }
            }
            return true;
        }

        /// <summary>
        /// this function assums that you have alread told both Axisi to stop moving otherwise it will timeout
        /// </summary>
        /// <returns>false if the telescope was still running at the end of the timeout</returns>
        private async Task<bool> WatTillStopped() {
            int mS_To_DecelerateAZ = (int)1.25 * (RunningCommand.AZ_Programed_Speed - AZStartSpeed) / RunningCommand.AZ_ACC;
            int mS_To_DecelerateEL = (int)1.25 * (RunningCommand.EL_Programed_Speed - AZStartSpeed) / RunningCommand.EL_ACC;
            int mS_To_Decelerate;
            if(mS_To_DecelerateAZ > mS_To_DecelerateEL) {
                mS_To_Decelerate = mS_To_DecelerateAZ;
            } else {
                mS_To_Decelerate = mS_To_DecelerateEL;
            }
            var timout = new CancellationTokenSource( mS_To_Decelerate ).Token;
            while(!timout.IsCancellationRequested) {
                var datatask = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 12 );
                await Task.Delay( 33 );
                var data = datatask.GetAwaiter().GetResult();
                bool isMoving = Is_Moing( data );
                if(!Is_Moing( data )) {
                    return true;
                }
            }
            return false;
        }

        public bool Is_Moing(ushort[] data) {
            try {
                bool azMoving = (((data[(int)MCUConstants.MCUOutputRegs.AZ_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.CCW_Motion) & 0b1) == 1) ||
                                (((data[(int)MCUConstants.MCUOutputRegs.AZ_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.CW_Motion) & 0b1) == 1);
                bool elMoving = (((data[(int)MCUConstants.MCUOutputRegs.EL_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.CCW_Motion) & 0b1) == 1) ||
                                (((data[(int)MCUConstants.MCUOutputRegs.EL_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.CW_Motion) & 0b1) == 1);
                return azMoving || elMoving;
            } catch {
                return false;
            }
        }


        public async Task<bool> Configure_MCU(MCUConfigurationAxys AZconfig, MCUConfigurationAxys ELconfig) {
            Current_AZConfiguration = AZconfig;
            Current_ELConfiguration = ELconfig;
            int gearedSpeedAZ = ConversionHelper.RPMToSPS(AZconfig.StartSpeed, MotorConstants.GEARING_RATIO_AZIMUTH);
            int gearedSpeedEL = ConversionHelper.RPMToSPS(ELconfig.StartSpeed, MotorConstants.GEARING_RATIO_ELEVATION);
            AZStartSpeed = gearedSpeedAZ;
            ELStartSpeed = gearedSpeedEL;
            TestDefaultParams(AZconfig.StartSpeed, ELconfig.StartSpeed, AZconfig.HomeTimeoutSec, ELconfig.HomeTimeoutSec);
            ushort[] data = {   MakeMcuConfMSW(AZconfig), MakeMcuConfLSW(AZconfig) , (ushort)(gearedSpeedAZ >> 0x0010), (ushort)(gearedSpeedAZ & 0xFFFF), 0x0,0x0,0x0,0x0,0x0,0x0,
                                MakeMcuConfMSW(ELconfig), MakeMcuConfLSW(ELconfig), (ushort)(gearedSpeedEL >> 0x0010), (ushort)(gearedSpeedEL & 0xFFFF), 0x0,0x0,0x0,0x0,0x0,0x0 };
            Console.WriteLine("start");
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_IMMEDIATE_STOP , MCUcomandType.IMIDEAT_STOP ) ).GetAwaiter().GetResult();
            Task.Delay(100).Wait();
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_CLEAR_MOVE , MCUcomandType.CLEAR_LAST_MOVE ) ).GetAwaiter().GetResult();
            Task.Delay(100).Wait();
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_RESET_ERRORS , MCUcomandType.RESET_ERRORS ) ).GetAwaiter().GetResult();
            Task.Delay(100).Wait();
            Send_Generic_Command_And_Track( new MCUcomand( data , MCUcomandType.CONFIGURE ) ).GetAwaiter().GetResult();
            Task.Delay(100).Wait();
            Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_CLEAR_MOVE , MCUcomandType.CLEAR_LAST_MOVE ) ).GetAwaiter().GetResult();
            Console.WriteLine("stop");
            return true;
        }

        private ushort MakeMcuConfMSW(MCUConfigurationAxys AxysConf) {
            ushort conf = 0x8400;//first byte should be 84 for current hardware setup
            switch (AxysConf.CWinput) {
                case CW_CCW_input_use.LimitSwitch:
                    conf = (ushort)(conf | 0b000_1000);
                    break;
                case CW_CCW_input_use.EStop:
                    conf = (ushort)(conf | 0b001_0000);
                    break;
            }
            switch (AxysConf.CCWinput) {
                case CW_CCW_input_use.LimitSwitch:
                    conf = (ushort)(conf | 0b0010_0000);
                    break;
                case CW_CCW_input_use.EStop:
                    conf = (ushort)(conf | 0b0100_0000);
                    break;
            }
            switch(AxysConf.EncoderType) {
                case EncoderTyprEnum.Quadrature_Encoder:
                    conf = (ushort)(conf | 0b1_0000_0000);
                    break;
                case EncoderTyprEnum.Diagnostic_Feedback:
                    conf = (ushort)(conf | 0b10_0000_0000);
                    break;
            }
            if (AxysConf.UseHomesensors) {
                conf = (ushort)(conf | 0b0100);
            }
            if(AxysConf.UseCapture) {
                conf = (ushort)(conf | 0b0001);
            }
            //conf = (ushort)(conf | 0b1000_0000);
            return conf;
        }

        private ushort MakeMcuConfLSW(MCUConfigurationAxys AxysConf) {
            ushort conf = 0x0000;
            if(AxysConf.CaptureActive_High) {
                conf = (ushort)(conf | 0b0001);
            }
            if (AxysConf.HomeActive_High) {
                conf = (ushort)(conf | 0b0_0100);
            }
            if (AxysConf.CWactive_High) {
                conf = (ushort)(conf | 0b0_1000);
            }
            if (AxysConf.CCWactive_High) {
                conf = (ushort)(conf | 0b1_0000);
            }

            return conf;
        }

        private void TestDefaultParams(double startSpeedDPSAzimuth, double startSpeedDPSElevation, int homeTimeoutSecondsAzimuth, int homeTimeoutSecondsElevation) {
            int gearedSpeedAZ = ConversionHelper.DPSToSPS(startSpeedDPSAzimuth, MotorConstants.GEARING_RATIO_AZIMUTH);
            int gearedSpeedEL = ConversionHelper.DPSToSPS(startSpeedDPSElevation, MotorConstants.GEARING_RATIO_ELEVATION);
            Console.WriteLine(gearedSpeedAZ.ToString() + " :AZ           EL:" + gearedSpeedEL.ToString());
            if ((gearedSpeedEL < 1) || (gearedSpeedEL > MCUConstants.ACTUAL_MCU_DEFAULT_PEAK_VELOCITY)) {
                throw new ArgumentOutOfRangeException("startSpeedDPSElevation", startSpeedDPSElevation,
                    String.Format("startSpeedDPSElevation should be between {0} and {1}",
                    ConversionHelper.SPSToDPS(1, MotorConstants.GEARING_RATIO_ELEVATION),
                    ConversionHelper.SPSToDPS(MCUConstants.ACTUAL_MCU_DEFAULT_PEAK_VELOCITY, MotorConstants.GEARING_RATIO_ELEVATION)));
            }
            if ((gearedSpeedAZ < 1) || (gearedSpeedAZ > MCUConstants.ACTUAL_MCU_DEFAULT_PEAK_VELOCITY)) {
                throw new ArgumentOutOfRangeException("startSpeedDPSAzimuth", startSpeedDPSAzimuth,
                    String.Format("startSpeedDPSAzimuth should be between {0} and {1}",
                    ConversionHelper.SPSToDPS(1, MotorConstants.GEARING_RATIO_AZIMUTH),
                    ConversionHelper.SPSToDPS(MCUConstants.ACTUAL_MCU_DEFAULT_PEAK_VELOCITY, MotorConstants.GEARING_RATIO_AZIMUTH)));
            }
            if ((homeTimeoutSecondsElevation < 0) || (homeTimeoutSecondsElevation > 300)) {
                throw new ArgumentOutOfRangeException("homeTimeoutSecondsElevation", homeTimeoutSecondsElevation,
                    String.Format("homeTimeoutSecondsElevation should be between {0} and {1}", 0, 300));
            }
            if ((homeTimeoutSecondsAzimuth < 0) || (homeTimeoutSecondsAzimuth > 300)) {
                throw new ArgumentOutOfRangeException("homeTimeoutSecondsAzimuth", homeTimeoutSecondsAzimuth,
                    String.Format("homeTimeoutSecondsAzimuth should be between {0} and {1}", 0, 300));
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="AZHomeCW"></param>
        /// <param name="ELHomeCW"></param>
        /// <param name="RPM"></param>
        /// <returns></returns>
        public async Task<bool> HomeBothAxyes( bool AZHomeCW , bool ELHomeCW , double RPM ) {
            int EL_Speed = ConversionHelper.DPSToSPS( ConversionHelper.RPMToDPS( RPM ) , MotorConstants.GEARING_RATIO_ELEVATION );
            int AZ_Speed = ConversionHelper.DPSToSPS( ConversionHelper.RPMToDPS( RPM ) , MotorConstants.GEARING_RATIO_AZIMUTH );
            ushort ACCELERATION = 50;
            ushort CWHome = 0x0020;
            ushort CcWHome = 0x0040;

            ushort azHomeDir = CcWHome;
            ushort elHomeDir = CcWHome;

            ushort elHomeSpeed = 0x0000;//this is the default value
            if(!ELHomeCW) {//if the MCU dosnt expect the home sensor to be high when a home is initiated this will need to be changed
                elHomeDir = CWHome;
                elHomeSpeed = 0x0040;//if this bit is set the MCU will home at its configured minimum speed
            }

            //set config word to 0x0040 to have the RT home at the minimumum speed
            ushort[] data = {
                azHomeDir , 0x0000      , 0x0000, 0x0000,(ushort)((AZ_Speed & 0xFFFF0000)>>16),(ushort)(AZ_Speed & 0xFFFF), ACCELERATION, ACCELERATION , 0x0000, 0x0000,
                //0x0000     ,0x0000      , 0x0000, 0x0000,0x0000                                ,0x0000                    , 0x0000       ,0x0000        ,0x0000 ,0x0000   
                elHomeDir , elHomeSpeed , 0x0000, 0x0000,(ushort)((EL_Speed & 0xFFFF0000)>>16),(ushort)(EL_Speed & 0xFFFF), ACCELERATION, ACCELERATION , 0x0000, 0x0000
            };

            Cancel_move();
            Task.Delay( 100 ).Wait();//wait to ensure it is porcessed
            Controled_stop();
            Task.Delay( 100 ).Wait();//wait to ensure it is porcessed
            MCUModbusMaster.WriteMultipleRegistersAsync( 1024 , data ).Wait();
            return true;
        }

        private ushort[] prepairRelativeMoveData(int SpeedAZ, int SpeedEL, ushort ACCELERATION, int positionTranslationAZ, int positionTranslationEL) {
            if (SpeedAZ < AZStartSpeed) {
                throw new ArgumentOutOfRangeException("SpeedAZ", SpeedAZ,
                    String.Format("SpeedAZ should be grater than {0} which is the stating speed set when configuring the MCU", AZStartSpeed));
            }
            if (SpeedEL < ELStartSpeed) {
                throw new ArgumentOutOfRangeException("SpeedEL", SpeedEL,
                    String.Format("SpeedAZ should be grater than {0} which is the stating speed set when configuring the MCU", ELStartSpeed));
            }
            ushort[] data = {
                0x0002 , 0x0003, (ushort)((positionTranslationAZ & 0xFFFF0000)>>16),(ushort)(positionTranslationAZ & 0xFFFF),(ushort)((SpeedAZ & 0xFFFF0000)>>16),(ushort)(SpeedAZ & 0xFFFF), ACCELERATION,ACCELERATION ,0,0,
                0x0002 , 0x0003, (ushort)((positionTranslationEL & 0xFFFF0000)>>16),(ushort)(positionTranslationEL & 0xFFFF),(ushort)((SpeedEL & 0xFFFF0000)>>16),(ushort)(SpeedEL & 0xFFFF), ACCELERATION,ACCELERATION ,0,0
            };
            return data;
        }

        public async Task<bool> MoveAndWaitForCompletion( int SpeedAZ , int SpeedEL , ushort ACCELERATION , int positionTranslationAZ , int positionTranslationEL ) {
            mCUpositon.update().Wait();
            var startPos =  mCUpositon as MCUpositonStore;
            Cancel_move();
            Task.Delay( 50 ).Wait();//wait to ensure it is porcessed
            ushort[] CMDdata = prepairRelativeMoveData( SpeedAZ , SpeedEL , ACCELERATION , positionTranslationAZ , positionTranslationEL );
            var ThisMove = Send_Generic_Command_And_Track( new MCUcomand( CMDdata , MCUcomandType.RELETIVE_MOVE ) { AZ_Programed_Speed = SpeedAZ , EL_Programed_Speed = SpeedEL , EL_ACC = ACCELERATION , AZ_ACC = ACCELERATION } ).GetAwaiter().GetResult();
            await Task.Delay( 50 );//wait for comand to be read

            int AZTime = estimateTime( SpeedAZ , ACCELERATION , positionTranslationAZ ), ELTime = estimateTime( SpeedEL , ACCELERATION , positionTranslationEL );
            int TimeToMove;
            if(AZTime > ELTime) {
                TimeToMove = AZTime;
            } else { TimeToMove = ELTime; }
            var timout = new CancellationTokenSource( (int)(TimeToMove * 1.2) );
            while(!timout.IsCancellationRequested) {
                var datatask = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 12 );
                await Task.Delay( 50 );
                var data = datatask.GetAwaiter().GetResult();
                bool azErr = ((data[(int)MCUConstants.MCUOutputRegs.AZ_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.Command_Error) & 0b1) == 1;
                bool elErr = ((data[(int)MCUConstants.MCUOutputRegs.EL_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.Command_Error) & 0b1) == 1;
                if(elErr || azErr) {//TODO:add more checks to this 
                    ThisMove.completed = true;
                    ThisMove.ComandError = new Exception( "MCU command error bit was set" );
                    consecutiveSucsefullMoves = 0;
                    consecutiveErrors++;
                    Send_Generic_Command_And_Track( new MCUcomand( MESSAGE_CONTENTS_RESET_ERRORS , MCUcomandType.RESET_ERRORS ) ).Wait();
                    timout.Dispose();
                    return false;
                }
                bool azFin = ((data[(int)MCUConstants.MCUOutputRegs.AZ_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.Move_Complete) & 0b1) == 1;
                bool elFin = ((data[(int)MCUConstants.MCUOutputRegs.EL_Status_Bist_MSW] >> (int)MCUConstants.MCUStutusBitsMSW.Move_Complete) & 0b1) == 1;
                bool isMoving = Is_Moing( data );
                if(azFin && elFin && !isMoving) {
                    //TODO:check that position is correct and there arent any errors

                    consecutiveSucsefullMoves++;
                    consecutiveErrors = 0;
                    ThisMove.completed = true;
                    timout.Dispose();
                    return true;
                }

            }
            var data2  = MCUModbusMaster.ReadHoldingRegistersAsync( 0 , 12 ).GetAwaiter().GetResult();
            if(Is_Moing( data2 )) {
                ThisMove.completed = true;
                ThisMove.ComandError = new Exception( "Move did not complete in the expected time" );
                consecutiveSucsefullMoves = 0;
                consecutiveErrors++;
            }
            timout.Dispose();
            return true;
        }

        public static int estimateTime(int maxVel, int acc, int dist) {
            //acc steps/millisecond/second
            //maxVel steps/second
            //dist steps
            //return millisecond
            int t1 = ((maxVel) / acc);//ms
            double t1s = t1 / 1000.0;
            int distT1 = (int)(((acc*1000) / 2) * (t1s * t1s) * 2);
            if (distT1 < dist) {
                int t2 = (dist - distT1) / maxVel;
                return t2*1000 + (2 * t1);
            } else {
                return 2 * t1;
            }
        }

        public bool Send_Jog_command( double AZspeed , bool AZClockwise , double ELspeed , bool ELClockwise ) {
            ushort dir;
            if(AZClockwise) {
                dir = 0x0080;
            } else dir = 0x0100;
            int AZstepSpeed = ConversionHelper.RPMToSPS( AZspeed , MotorConstants.GEARING_RATIO_AZIMUTH );
            int ELstepSpeed = ConversionHelper.RPMToSPS( ELspeed , MotorConstants.GEARING_RATIO_ELEVATION );
            ushort[] data = new ushort[10] { dir , 0x0003 , 0x0 , 0x0 , (ushort)(AZstepSpeed >> 16) , (ushort)(AZstepSpeed & 0xffff) , MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_WITH_GEARING , MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_WITH_GEARING , 0x0 , 0x0 , };
            ushort[] data2 = new ushort[20];

            if(AZstepSpeed > AZStartSpeed) {
                //throw new ArgumentOutOfRangeException( "speed" , AZspeed ,
                //    String.Format( "speed should be grater than {0} which is the stating speed set when configuring the MCU" ,
                //    ConversionHelper.SPSToRPM( AZStartSpeed , MotorConstants.GEARING_RATIO_AZIMUTH ) ) );
                for(int j = 0; j < data.Length; j++) {
                    data2[j] = data[j];
                }
            } else {
                AZstepSpeed = 0;
            }

            if(ELstepSpeed > ELStartSpeed) {
                //throw new ArgumentOutOfRangeException( "speed" , ELspeed ,
                //    String.Format( "speed should be grater than {0} which is the stating speed set when configuring the MCU" ,
                //    ConversionHelper.SPSToRPM( ELStartSpeed , MotorConstants.GEARING_RATIO_AZIMUTH ) ) );
                for(int j = 0; j < data.Length; j++) {
                    data2[j + 10] = data[j];
                }
                if(ELClockwise) {
                    dir = 0x0080;
                } else dir = 0x0100;

                data2[10] = (ushort)(dir);
                data2[14] = (ushort)(ELstepSpeed >> 16);
                data2[15] = (ushort)(ELstepSpeed & 0xffff);
            } else {
                ELstepSpeed = 0;
            }
            _ = Send_Generic_Command_And_Track( new MCUcomand( data2 , MCUcomandType.JOG ) {
                AZ_Programed_Speed = AZstepSpeed ,
                EL_Programed_Speed = ELstepSpeed ,
                EL_ACC = MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_WITH_GEARING ,
                AZ_ACC = MCUConstants.ACTUAL_MCU_MOVE_ACCELERATION_WITH_GEARING
            } ).GetAwaiter().GetResult();
            return true;
        }

        private async Task<MCUcomand> Send_Generic_Command_And_Track( MCUcomand incoming ) {
            if(RunningCommand.ComandType == MCUcomandType.JOG) {
                if(incoming.ComandType == MCUcomandType.CLEAR_LAST_MOVE || incoming.ComandType == MCUcomandType.IMIDEAT_STOP || incoming.ComandType == MCUcomandType.JOG) {
                    RunningCommand = incoming;
                    MCUModbusMaster.WriteMultipleRegistersAsync( MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS , incoming.comandData ).Wait();
                    return incoming;
                }
                incoming.ComandError = new Exception( "MCU was running a JOG move which could not be overriden" );
                return incoming;
            } else if((RunningCommand.ComandType == MCUcomandType.HOME && !RunningCommand.completed)) {
                if( incoming.ComandType == MCUcomandType.IMIDEAT_STOP) {
                    RunningCommand = incoming;
                    MCUModbusMaster.WriteMultipleRegistersAsync( MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS , incoming.comandData ).Wait();
                    return incoming;
                }
                incoming.ComandError = new Exception( "MCU was running a home move which could not be overriden" );
                return incoming;
            }

            RunningCommand = incoming;
            MCUModbusMaster.WriteMultipleRegistersAsync( MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS , incoming.comandData ).Wait();
            return incoming;
        }


        public RadioTelescopeAxisEnum Is_jogging(){
            ushort[] data = MCUModbusMaster.ReadHoldingRegisters( MCUConstants.ACTUAL_MCU_WRITE_REGISTER_START_ADDRESS , 20 );
            //word 0 and 10 indicate a jog comand for a specific axis however if the position registers(2,3  && 12,13) have non zero value then its a REGISTRATION MOVE
            if((data[10] == 0x0100 || data[10] == 0x0080) && (data[0] == 0x0100 || data[0] == 0x0080) && data[2] == 0x0 && data[3] == 0x0 && data[12] == 0x0 && data[13] == 0x0)//both jogging
            {
                return RadioTelescopeAxisEnum.BOTH;
            } else if((data[10] == 0x0100 || data[10] == 0x0080) && data[12] == 0x0 && data[13] == 0x0)//el is jogging
              {
                return RadioTelescopeAxisEnum.ELEVATION;
            } else if((data[0] == 0x0100 || data[0] == 0x0080) && data[2] == 0x0 && data[3] == 0x0)//az is jogging
              {
                return RadioTelescopeAxisEnum.AZIMUTH;
            } else return RadioTelescopeAxisEnum.UNKNOWN;
        }


        public long getLastContact() {
            return MCU_last_contact;
        }


        private bool Int_to_bool( int val ) {
            logger.Info( val );
            if(val == 0) {
                return false;
            } else { return true; }
        }
    }
}

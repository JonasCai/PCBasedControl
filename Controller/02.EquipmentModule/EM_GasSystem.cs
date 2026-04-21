

using Common.Recipe;
using Controller._01.ControlModule;
using Controller.EventLogger;
using Controller.Hardware;
using Controller.S88;

namespace Controller._02.EquipmentModule;

public class EM_GasSystem : S88EquipmentModuleBase
{
    private GasSystemAction _selectedAction = GasSystemAction.None;
    private IRecipeStep? _currentRecipeStep; // 缓存当前正在执行的配方参数


    public EM_GasSystem(EquipmentModuleCfg cfg, ILogger<S88EquipmentModuleBase> logger, IEventProducer eventProducer,
        IONodes iONodes,
        IMfcFactory mfcFactory,
        IScaleAIFactory scaleAIFactory,
        ICheckSensorFactory checkSensorFactory,
        ITempControllerFactory tempControllerFactory,
        IValveFactory valveFactory) : base(cfg.Name, eventProducer, logger)
    {
        RegisterMfcs(iONodes, mfcFactory);
        RegisterValvers(iONodes, valveFactory);
        RegisterScaleAIs(iONodes, scaleAIFactory);
        RegisterCheckSensors(iONodes, checkSensorFactory);
        RegisterTempControllers(iONodes, tempControllerFactory);
    }

    // 绕过字符串 Command 队列，提升实时性
    public void StartRecipeAction(GasSystemAction action, IRecipeStep stepParams)
    {
        if (State == EMState.Error) return;

        _selectedAction = action;
        _currentRecipeStep = stepParams;
        ChangeState(EMState.Busy); 
    }

    protected override void OnExecute()
    {
        if (State != EMState.Busy) return;

        switch (_selectedAction)
        {
            case GasSystemAction.SetupReactionZone:
                ExecuteReactionZoneSetup();
                break;
            case GasSystemAction.PumpDown:
                ExecutePumpDownSetup();
                break;
            case GasSystemAction.Vent:
                ExecuteVentSetup();
                break;
            default:
                Step = 0;
                break;
        }
    }

    private void ExecuteReactionZoneSetup()
    {
        var step = (ReactionZoneStep)_currentRecipeStep!;
        // 空间型 ALD 的做法：根据 step 的 CarrierA, CarrierB, Isolation 参数，配置多个 MFC 和分流阀

        ChangeState(EMState.Idle);
    }
    private void ExecutePumpDownSetup()
    {
        
    }
    private void ExecuteVentSetup()
    {
        
    }


    #region Valve
    private CM_Valve PV102 = null!;
    private CM_Valve PV103 = null!;
    private CM_Valve PV104 = null!;
    private CM_Valve PV105 = null!;
    private CM_Valve PV111 = null!;
    private CM_Valve PV112 = null!;
    private CM_Valve PV113 = null!;
    private CM_Valve PV114 = null!;
    private CM_Valve PV115 = null!;
    private CM_Valve PV116 = null!;
    private CM_Valve PV117 = null!;
    private CM_Valve PV118 = null!;
    private CM_Valve PV119 = null!;
    private CM_Valve PV121 = null!;
    private CM_Valve PV122 = null!;
    private CM_Valve PV123 = null!;
    private CM_Valve PV301 = null!;
    private CM_Valve PV302 = null!;
    private CM_Valve PV303 = null!;
    private void RegisterValvers(IONodes iONodes, IValveFactory valveFactory)
    {
        PV102 = RegisterValve("PV102", valveFactory, () => iONodes.ESV01[0] = true, () => iONodes.ESV01[0] = false, canOpen: () => true, canClose: () => true);
        PV103 = RegisterValve("PV103", valveFactory, () => iONodes.A110[13] = true, () => iONodes.A110[13] = false, canOpen: () => true, canClose: () => true);
        PV104 = RegisterValve("PV104", valveFactory, () => iONodes.A110[12] = true, () => iONodes.A110[12] = false, canOpen: () => true, canClose: () => true);
        PV105 = RegisterValve("PV105", valveFactory, () => iONodes.ESV01[19] = true, () => iONodes.ESV01[19] = false, canOpen: () => true, canClose: () => true);
        PV111 = RegisterValve("PV111", valveFactory, () => iONodes.ESV01[2] = true, () => iONodes.ESV01[2] = false, canOpen: () => true, canClose: () => true);
        PV112 = RegisterValve("PV112", valveFactory, () => iONodes.ESV01[4] = true, () => iONodes.ESV01[4] = false, canOpen: () => true, canClose: () => true);
        PV113 = RegisterValve("PV113", valveFactory, () => iONodes.ESV01[6] = true, () => iONodes.ESV01[6] = false, canOpen: () => true, canClose: () => true);
        PV114 = RegisterValve("PV114", valveFactory, () => iONodes.ESV01[8] = true, () => iONodes.ESV01[8] = false, canOpen: () => true, canClose: () => true);
        PV115 = RegisterValve("PV115", valveFactory, () => iONodes.ESV01[10] = true, () => iONodes.ESV01[10] = false, canOpen: () => true, canClose: () => true);
        PV116 = RegisterValve("PV116", valveFactory, () => iONodes.ESV01[12] = true, () => iONodes.ESV01[12] = false, canOpen: () => true, canClose: () => true);
        PV117 = RegisterValve("PV117", valveFactory, () => iONodes.ESV01[14] = true, () => iONodes.ESV01[14] = false, canOpen: () => true, canClose: () => true);
        PV118 = RegisterValve("PV118", valveFactory, () => iONodes.ESV01[3] = true, () => iONodes.ESV01[3] = false, canOpen: () => true, canClose: () => true);
        PV119 = RegisterValve("PV119", valveFactory, () => iONodes.ESV01[16] = true, () => iONodes.ESV01[16] = false, canOpen: () => true, canClose: () => true);
        PV121 = RegisterValve("PV121", valveFactory, () => iONodes.ESV01[18] = true, () => iONodes.ESV01[18] = false, canOpen: () => true, canClose: () => true);
        PV122 = RegisterValve("PV122", valveFactory, () => iONodes.ESV01[15] = true, () => iONodes.ESV01[15] = false, canOpen: () => true, canClose: () => true);
        PV123 = RegisterValve("PV123", valveFactory, () => iONodes.ESV01[17] = true, () => iONodes.ESV01[17] = false, canOpen: () => true, canClose: () => true);
        PV301 = RegisterValve("PV301", valveFactory, () => iONodes.ESV01[9] = true, () => iONodes.ESV01[9] = false, canOpen: () => true, canClose: () => true);
        PV302 = RegisterValve("PV302", valveFactory, () => iONodes.ESV01[11] = true, () => iONodes.ESV01[11] = false, canOpen: () => true, canClose: () => true);
        PV303 = RegisterValve("PV303", valveFactory, () => iONodes.ESV01[13] = true, () => iONodes.ESV01[13] = false, canOpen: () => true, canClose: () => true);
    }
    #endregion

    #region MFC
    private CM_MFC MFC111 = null!;
    private CM_MFC MFC112 = null!;
    private CM_MFC MFC121 = null!;
    private CM_MFC MFC122 = null!;
    private CM_MFC MFC131 = null!;
    private CM_MFC MFC132 = null!;
    private void RegisterMfcs(IONodes iONodes, IMfcFactory mfcFactory)
    {
        MFC111 = RegisterMfc("MFC111", mfcFactory, iONodes.MFC111, 5000f);
        MFC112 = RegisterMfc("MFC112", mfcFactory, iONodes.MFC112, 5000f);
        MFC121 = RegisterMfc("MFC121", mfcFactory, iONodes.MFC121, 5000f);
        MFC122 = RegisterMfc("MFC122", mfcFactory, iONodes.MFC122, 1000f);
        MFC131 = RegisterMfc("MFC131", mfcFactory, iONodes.MFC131, 50000f);
        MFC132 = RegisterMfc("MFC132", mfcFactory, iONodes.MFC132, 50000f);
    }
    #endregion

    #region ScaleAI
    private CM_ScaleAI RegisterScaleAI(
        string name,
        IScaleAIFactory factory,
        Func<float> readRawValue,
        float rawMin, float rawMax,
        float scaledMin, float scaledMax,
        string unit,
        float filterAlpha = 1.0f)
    {
        var cfg = new ScaleAICfg
        {
            Name = name,
            ReadRawValue = readRawValue,
            RawMin = rawMin,
            RawMax = rawMax,
            ScaledMin = scaledMin,
            ScaledMax = scaledMax,
            EngineeringUnit = unit,
            FilterAlpha = filterAlpha
        };

        var ai = factory.Create(cfg);
        RegisterCm(ai);
        return ai;
    }
    private CM_ScaleAI PT111 = null!;
    private CM_ScaleAI PT121 = null!;
    private CM_ScaleAI H2OLI = null!;//Level Indicator
    private void RegisterScaleAIs(IONodes iONodes, IScaleAIFactory scaleAIFactory)
    {
        //Sn源出口
        PT111 = RegisterScaleAI("PT111", scaleAIFactory,
            readRawValue: () => iONodes.A205[0],
            rawMin: 0, rawMax: 4000,
            scaledMin: 0f, scaledMax: 133.322f,
            unit: "kPa");

        //H2O源出口
        PT121 = RegisterScaleAI("PT121", scaleAIFactory,
            readRawValue: () => iONodes.A205[1],
            rawMin: 0, rawMax: 4000,
            scaledMin: 0f, scaledMax: 133.322f,
            unit: "kPa");

        //H2O源出口
        H2OLI = RegisterScaleAI("H2OLI", scaleAIFactory,
            readRawValue: () => iONodes.A206[0],
            rawMin: 0, rawMax: 8000,
            scaledMin: 0f, scaledMax: 263f,
            unit: "kPa");
    }
    #endregion

    #region CheckSensor
    private CM_CheckSensor WaterBubbler_H = null!;
    private CM_CheckSensor WaterBubbler_L = null!;
    private CM_CheckSensor H2OSourceLevel_A = null!;
    private CM_CheckSensor H2OSourceLevel_B = null!;
    private CM_CheckSensor H2OSourceLevel_C = null!;
    private CM_CheckSensor H2OSourceLevel_D = null!;
    private CM_CheckSensor H2OSourceLevel_Fault = null!;
    private CM_CheckSensor SnOverTemp_Detect = null!;
    private CM_CheckSensor H2OOverTemp_Detect = null!;
    private CM_CheckSensor CH4Leak_Detect = null!;
    private CM_CheckSensor Flame_Detect = null!;
    private CM_CheckSensor Smoke_Heat_Detect = null!;
    private CM_CheckSensor SnCabinetDoorSwitch = null!;
    private void RegisterCheckSensors(IONodes iONodes, ICheckSensorFactory checkSensorFactory)
    {
        //贮水罐液位高
        WaterBubbler_H = RegisterCheckSensor("WaterBubbler_H", checkSensorFactory,
            readSignal: () => iONodes.A201[0]);

        //贮水罐液位低
        WaterBubbler_L = RegisterCheckSensor("WaterBubbler_L", checkSensorFactory,
            readSignal: () => iONodes.A201[1]);

        //水源瓶液位
        H2OSourceLevel_A = RegisterCheckSensor("H2OSourceLevel_A", checkSensorFactory,
            readSignal: () => iONodes.A201[2]);

        //水源瓶液位
        H2OSourceLevel_B = RegisterCheckSensor("H2OSourceLevel_B", checkSensorFactory,
            readSignal: () => iONodes.A201[3]);

        //水源瓶液位
        H2OSourceLevel_C = RegisterCheckSensor("H2OSourceLevel_C", checkSensorFactory,
            readSignal: () => iONodes.A201[4]);

        //水源瓶液位
        H2OSourceLevel_D = RegisterCheckSensor("H2OSourceLevel_D", checkSensorFactory,
            readSignal: () => iONodes.A201[5]);

        //水源瓶液位
        H2OSourceLevel_Fault = RegisterCheckSensor("H2OSourceLevel_Fault", checkSensorFactory,
            readSignal: () => iONodes.A201[6]);

        SnCabinetDoorSwitch = RegisterCheckSensor("SnCabinetDoorSwitch", checkSensorFactory,
            readSignal: () => iONodes.A201[7],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        CH4Leak_Detect = RegisterCheckSensor("CH4Leak_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[8],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        Flame_Detect = RegisterCheckSensor("Flame_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[9],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        Smoke_Heat_Detect = RegisterCheckSensor("Smoke_Heat_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[10],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        SnOverTemp_Detect = RegisterCheckSensor("SnOverTemp_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[11],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);

        H2OOverTemp_Detect = RegisterCheckSensor("H2OOverTemp_Detect", checkSensorFactory,
            readSignal: () => iONodes.A201[12],
            defaultExpectedSignalState: ExpectedSignalState.ShouldBeOn,
            autoStart: true);
    }
    #endregion

    #region TempController
    private CM_TempController SnHeateringZoneJ = null!;
    private CM_TempController SnHeateringZoneI = null!;
    private CM_TempController SnHeateringZoneH = null!;
    private CM_TempController SnHeateringZoneG = null!;
    private CM_TempController SnHeateringZone1 = null!;
    private CM_TempController SnHeateringZone2 = null!;
    private CM_TempController SnHeateringZone3 = null!;
    private CM_TempController SnHeateringZone4 = null!;
    private CM_TempController SnHeateringZone5 = null!;
    private CM_TempController SnHeateringZone6_1 = null!;
    private CM_TempController SnHeateringZone6_2 = null!;
    private CM_TempController SnHeateringZone7 = null!;
    private CM_TempController SnHeateringZone8 = null!;
    private CM_TempController SnHeateringZone9 = null!;
    private CM_TempController SnHeateringZone10 = null!;
    private CM_TempController SnHeateringZone11 = null!;
    private CM_TempController SnHeateringZone12 = null!;
    private void RegisterTempControllers(IONodes iONodes, ITempControllerFactory tempControllerFactory)
    {
        //Sn源 - G
        SnHeateringZoneG = RegisterTempController("SnHeateringZoneG", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[0],
            setHeaterOn: (on) => iONodes.A203[0] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - H
        SnHeateringZoneH = RegisterTempController("SnHeateringZoneH", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[1],
            setHeaterOn: (on) => iONodes.A203[1] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - I
        SnHeateringZoneI = RegisterTempController("SnHeateringZoneI", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[2],
            setHeaterOn: (on) => iONodes.A203[2] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - J
        SnHeateringZoneJ = RegisterTempController("SnHeateringZoneJ", tempControllerFactory,
            readControlTemperature: () => iONodes.A207[3],
            setHeaterOn: (on) => iONodes.A203[3] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 1
        SnHeateringZone1 = RegisterTempController("SnHeateringZone1", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[0],
            setHeaterOn: (on) => iONodes.A203[4] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 2
        SnHeateringZone2 = RegisterTempController("SnHeateringZone2", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[1],
            setHeaterOn: (on) => iONodes.A203[5] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 3
        SnHeateringZone3 = RegisterTempController("SnHeateringZone3", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[2],
            setHeaterOn: (on) => iONodes.A203[6] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 4
        SnHeateringZone4 = RegisterTempController("SnHeateringZone4", tempControllerFactory,
            readControlTemperature: () => iONodes.A208[3],
            setHeaterOn: (on) => iONodes.A203[7] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 5
        SnHeateringZone5 = RegisterTempController("SnHeateringZone5", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[0],
            setHeaterOn: (on) => iONodes.A203[8] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 6-1
        SnHeateringZone6_1 = RegisterTempController("SnHeateringZone6_1", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[1],
            setHeaterOn: (on) => iONodes.A203[9] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 6-2
        SnHeateringZone6_2 = RegisterTempController("SnHeateringZone6_2", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[2],
            setHeaterOn: (on) => iONodes.A203[10] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 7
        SnHeateringZone7 = RegisterTempController("SnHeateringZone7", tempControllerFactory,
            readControlTemperature: () => iONodes.A210[3],
            setHeaterOn: (on) => iONodes.A203[11] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 8
        SnHeateringZone8 = RegisterTempController("SnHeateringZone8", tempControllerFactory,
            readControlTemperature: () => iONodes.A211[0],
            setHeaterOn: (on) => iONodes.A203[12] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 9
        SnHeateringZone9 = RegisterTempController("SnHeateringZone9", tempControllerFactory,
            readControlTemperature: () => iONodes.A211[1],
            setHeaterOn: (on) => iONodes.A203[13] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 10
        SnHeateringZone10 = RegisterTempController("SnHeateringZone10", tempControllerFactory,
            readControlTemperature: () => iONodes.A216[0],
            setHeaterOn: (on) => iONodes.A204[0] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 11 - 进口阀
        SnHeateringZone11 = RegisterTempController("SnHeateringZone11", tempControllerFactory,
            readControlTemperature: () => iONodes.A214[2],
            setHeaterOn: (on) => iONodes.A204[11] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);

        //Sn源 - 12 - 出口阀
        SnHeateringZone12 = RegisterTempController("SnHeateringZone12", tempControllerFactory,
            readControlTemperature: () => iONodes.A214[3],
            setHeaterOn: (on) => iONodes.A204[12] = on,
            canExecute: () => true,
            maxSafeTemp: 150f);
    }
    #endregion
}

public enum GasSystemAction
{
    None,
    Pulse,              // 对应 PulseStep
    Purge,              // 对应 PurgeStep
    SetupReactionZone,  // 对应 ReactionZoneStep (空间型ALD)
    PumpDown,           // 对应 PumpDownStep 抽空
    Vent,                // 对应 VentStep 破空
    PurgeLine,          //管路吹扫
}

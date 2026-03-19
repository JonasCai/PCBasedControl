using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Recipe;

// 指定 JSON 中用来区分类型的字段名为 "StepType"
[JsonPolymorphic(TypeDiscriminatorPropertyName = "StepType")] 
[JsonDerivedType(typeof(PulseStep), typeDiscriminator: "Pulse")] 
[JsonDerivedType(typeof(PurgeStep), typeDiscriminator: "Purge")] 
[JsonDerivedType(typeof(ReactionZoneStep), typeDiscriminator: "ReactionZone")] 
[JsonDerivedType(typeof(MoveAxisStep), typeDiscriminator: "MoveAxis")] 
[JsonDerivedType(typeof(WaitStep), typeDiscriminator: "Wait")] 
[JsonDerivedType(typeof(CycleStep), typeDiscriminator: "Cycle")] 
[JsonDerivedType(typeof(PumpDownStep), typeDiscriminator: "PumpDown")] 
[JsonDerivedType(typeof(VentStep), typeDiscriminator: "Vent")] 
[JsonDerivedType(typeof(SetPressureStep), typeDiscriminator: "SetPressure")] 
[JsonDerivedType(typeof(WaitPressureStableStep), typeDiscriminator: "WaitPressureStable")] 
[JsonDerivedType(typeof(SetTemperatureStep), typeDiscriminator: "SetTemperature")] 
[JsonDerivedType(typeof(WaitTemperatureStableStep), typeDiscriminator: "WaitTemperatureStable")] 
public interface IRecipeStep 
{ 
    StepType StepType { get; } 
}

public enum StepType 
{ 
    Pulse, // 脉冲（时间型ALD）
    Purge, // 吹扫
    ReactionZone, // 反应区（空间型ALD）
    MoveAxis, // 运动轴控制 (空间型ALD基底移动)
    Wait, // 纯粹的时间等待
    Cycle, // 循环
    Vent, // 破空
    PumpDown, // 抽空
    SetTemperature, // 设定真空腔室温度
    WaitTemperatureStable, // 等待温度稳定
    SetPressure, // 设定真空腔室压力
    WaitPressureStable, // 等待压力稳定
} 

public class PulseStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.Pulse; 
    public required string TargetReactant { get; set; } 
    public TimeSpan PulseTime { get; set; } 
    public float CarrierGasFlowSccm { get; set; } = 200.0f; 
} 

public class PurgeStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.Purge; 
    public TimeSpan Duration { get; set; } 
    public float PurgeGasFlowSccm { get; set; } = 200.0f; 
} 

public class ReactionZoneStep : IRecipeStep 
{ 
    [JsonIgnore] 
    public StepType StepType { get; } = StepType.ReactionZone; 
    public float CarrierGasAFlowSccm { get; set; } = 200.0f; 
    public float DilutionGasAFlowSccm { get; set; } = 0.0f; 
    public float CarrierGasBFlowSccm { get; set; } = 200.0f; 
    public float DilutionGasBFlowSccm { get; set; } = 0.0f; 
    public float IsolationGasFlowSccm { get; set; } = 10000.0f; 
} 

public class MoveAxisStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.MoveAxis; 
    public required string TargetAxis { get; set; } 
    public float TargetPosition { get; set; } 
    public float TargetVelocity { get; set; } 
} 

public class CycleStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.Cycle; 
    public uint LoopCount { get; set; } 
    public List<IRecipeStep> SubSteps { get; set; } = new List<IRecipeStep>(); 
} 

public class WaitStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.Wait; 
    public TimeSpan Duration { get; set; } 
} 

public class PumpDownStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.PumpDown; 
    public float TargetPressurePa { get; set; } = 100.0f; 
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3); 
} 

public class VentStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.Vent; 
    public float TargetPressurePa { get; set; } = 101325f; 
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3); 
} 

public class SetPressureStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.SetPressure; 
    public double TargetPressurePa { get; set; } 
    public double TolerancePa { get; set; } = 3.0; 
} 

public class WaitPressureStableStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.WaitPressureStable; 
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10); 
    public TimeSpan StableDuration { get; set; } = TimeSpan.FromSeconds(30); 
    public double TargetPressurePa { get; set; } = 10.0f; 
    public double TolerancePa { get; set; } = 3.0; 
} 

public class SetTemperatureStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.SetTemperature; 
    public double TargetTemperatureCelsius { get; set; } = 200.0f; 
    public double ToleranceCelsius { get; set; } = 1.0; 
} 

public class WaitTemperatureStableStep : IRecipeStep 
{ 
    [JsonIgnore] public StepType StepType { get; } = StepType.WaitTemperatureStable; 
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10); 
    public TimeSpan StableDuration { get; set; } = TimeSpan.FromSeconds(30); 
    public double TargetTemperatureCelsius { get; set; } = 200.0f; 
    public double ToleranceCelsius { get; set; } = 1.0; 
}

[JsonConverter(typeof(JsonStringEnumConverter))] 
public enum AldMode 
{ 
    Temporal, // 时间型 ALD
    Spatial // 空间型 ALD
} 

public class AldRecipe 
{ 
    public required string RecipeName { get; set; } 
    public AldMode Mode { get; set; } 
    public List<IRecipeStep> Steps { get; set; } = new(); 
}

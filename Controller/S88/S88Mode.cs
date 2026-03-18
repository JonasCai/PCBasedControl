using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.S88;

public enum S88Mode
{
    Manual,         // 手动：状态机逻辑停转，并在HMI开放底层设备控制权
    SemiAuto,       // 半自动：跑自动逻辑，但每一步都需要人工确认 (Step-by-step)
    Automatic       // 自动：全自动运行逻辑
}

public enum S88State
{
    SystemFault,// 系统故障, 联系工程师

    // ============= WAIT STATE (等待命令) =============
    /* This is the state that indicates that RESETTING is complete. The machine will maintain the 
     * conditions that were achieved during the RESETTING state, and perform operations required 
     * when the machine is in IDLE. */
    Idle,           // 初始/空闲

    /*Refer to HOLDING for when this state is used. In this state the machine does not
    produce product. It will either stop running or continue to dry cycle. A transition to the
    UNHOLDING state will occur when internal machine conditions change or an Unhold
    command is initiated by an operator.*/
    Held,           // 已保持 (安全暂停，通常指故障导致)

    /*The machine maintains status information relevant to the abort condition. The
    machine can only exit the ABORTED state after an explicit Clear command,
    subsequent to manual intervention to correct and reset the detected machine faults.*/
    Aborted,        // 已中止 (紧急停止后)

    /*The machine has finished the COMPLETING state and is now waiting for a Reset
    command before transitioning to the RESETTING state.*/
    Completed,       // 完成 (正常结束)

    /*The machine is powered and stationary after completing the STOPPING state. All communications 
     * with other systems are functioning (if applicable). A Reset command will cause a transition 
     * from STOPPED to the RESETTING state.*/
    Stopped,        // 已停止

    /* Refer to SUSPENDING for when this state is used. In this state the machine does not
    produce product. It will either stop running or continue to cycle without producing until
    external process conditions return to normal, at which time, the SUSPENDED state
    will transition to the UNSUSPENDING state, typically without any operator
    intervention. */
    Suspended,  //已挂起(外部原因，上游缺料、下游堵料)

    // ============= ACTING STATE (执行逻辑) =============
    /*Once the machine is processing materials it is in the EXECUTE state until a transition
    command is received. Different machine modes will result in specific types of
    EXECUTE activities. For example, if the machine is in the “Production” mode, the
    EXECUTE will result in products being produced, while perhaps in a user-defined
    “Clean Out” mode the EXECUTE state would result in the action of cleaning the
    machine.*/
    Execute,        // 运行中

    /*This state is the result of a Reset command from the STOPPED or COMPLETE state.
    Faults and stop causes are reset. RESETTING will typically cause safety devices to
    be energized and place the machine in the IDLE state where it will wait for a Start
    command. No hazardous motion should happen in this state.*/
    Resetting,      // 复位中

    /*,The machine completes the steps needed to start. This state is entered as a result of a 
     * Start command (local or remote). When STARTING completes, the machine will transition to 
     * the EXECUTE state.*/
    Starting,       // 启动中

    /*This state is used when internal (inside this unit/machine and not from another
    machine on the production line) machine conditions do not allow the machine to
    continue producing, that is, the machine leaves the EXECUTE or SUSPENDED states
    due to internal conditions. This is typically used for routine machine conditions that
    require minor operator servicing to continue production. This state can be initiated
    automatically or by an operator and can be easily recovered from. An example of this
    would be a machine that requires an operator to periodically refill a glue dispenser or
    carton magazine and due to the machine design, these operations cannot be
    performed while the machine is running. Since these types of tasks are normal
    production operations, it is not desirable to go through aborting or stopping
    sequences, and because these functions are integral to the machine they are not
    considered to be “external.” While in the HOLDING state, the machine is typically
    brought to a controlled stop and then transitions to HELD upon state complete. To be
    able to restart production correctly after the HELD state, all relevant process set
    points and return status of the procedures at the time of receiving the Hold command
    must be saved in the machine controller when executing the HOLDING procedure.*/
    Holding,        // 保持中

    /*Refer to HOLDING for when this state is used. A machine will typically enter into
    UNHOLDING automatically when internal conditions, material levels, for example,
    return to an acceptable level. If an operator is required to perform minor servicing to
    replenish materials or make adjustments, then the Unhold command may be initiated
    by the operator.*/
    Unholding,      // 解除保持中 

    /*The ABORTING state can be entered at any time in response to the Abort command,
    typically triggered by the occurrence of a machine event that warrants an aborting
    action. The aborting logic will bring the machine to a rapid safe stop.*/
    Aborting,       // 中止中

    /*This state is entered in response to a Stop command. While in this state the machine
    executes the logic that brings it to a controlled stop as reflected by the STOPPED
    state. Normal STARTING of the machine cannot be initiated unless RESETTING has
    taken place.*/
    Stopping,       // 停止中

    /*This state is the result of a Complete command from the EXECUTE, HELD or
    SUSPENDED states. The Complete command may be internally generated, such as
    reaching the end of a predefined production count where normal operation has run to
    completion, or externally generated, such as by a supervisory system. The
    COMPLETING state is often used to end a production run and summarize production
    data.*/
    Completing,      // 完成中 

    /* This state is used when external (outside this unit/machine but usually on the same
    integrated production line) process conditions do not allow the machine to continue
    producing, that is, the machine leaves EXECUTE due to upstream or downstream
    conditions on the line. This is typically due to a Blocked or Starved event. This
    condition may be detected by a local machine sensor or based on a supervisory
    system external command. While in the SUSPENDING state, the machine is typically
    brought to a controlled stop and then transitions to SUSPENDED upon state
    complete. To be able to restart production correctly after the SUSPENDED state, all
    relevant process set points and return status of the procedures at the time of
    receiving the Suspend command must be saved in the machine controller when
    executing the SUSPENDING procedure.*/
    Suspending,  //挂起中

    /*Refer to SUSPENDING for when this state is used. This state is a result of process
    conditions returning to normal. The UNSUSPENDING state initiates any required
    actions or sequences necessary to transition the machine from SUSPENDED back to
    EXECUTE. To be able to restart production correctly after the SUSPENDED state, all
    relevant process set points and return status of the procedures at the time of
    receiving the Suspend command must be saved in the machine controller when
    executing the SUSPENDING procedure.  */
    Unsuspending,//接触挂起中

    /*Initiated by a Clear command to clear faults that may have occurred and are present
    in the ABORTED state before proceeding to a STOPPED state.*/
    Clearing //故障清除中
}

public enum S88Command
{
    Start,
    Reset,
    Hold,
    Unhold,
    Suspend,
    Unsuspend,
    Complete,
    Clear,
    Stop,
    Abort
}

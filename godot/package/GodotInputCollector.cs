// GodotInputCollector：Godot 输入采集（对仗 unity YioInputCollector）。
//
// 模型差异：Unity 轮询（Input System 每帧读状态），Godot 事件驱动——本类在
// Driver._Input 累积帧内事件（指针边沿/键盘/滚轮/文本），CollectInput 帧消费清空
// （对仗 Unity _textBuf 的「两帧之间多次、每帧消费」模式）。
//
// 契约对齐点：
//   - PointerEvent：kind 0=Down/1=Up/2=Move/3=Cancel，button 0/1/2=左/中/右（web 同族），
//     坐标 = design（窗口=设计分辨率 1:1、top-left y-down——Godot viewport 坐标系同构）。
//     Unity 每帧恰发一条（边沿 or Move）；Godot 帧内可多条边沿（快速点击），逐条入数组
//     一次 set_input（协议本就支持 n 条），无边沿帧发一条 Move（hover 状态机依赖每帧位置）。
//   - KeyEvent：key_code = YioKeyCode 数值族（GLFW 同源；Godot.Key 枚举不同族，映射表
//     见 MapKey——Yio.Types.cs 契约注释预留「Godot：Godot.Key → 本表映射」）。
//     OS 键盘重复：Godot InputEventKey.Echo 自带（Unity 两代输入都不发须 KeyRepeatState
//     合成；Godot 无此问题，echo 直接转发 keydown）。
//   - 文本：InputEventKey.Unicode（按布局含 shift 效果）→ set_text_input（UTF-32 码点）。
//     echo（自动重复）不重发字符——对齐 Unity「长按字母只重发 keydown 不重复插字」。
//   - 滚轮：Godot 每 notch 一事件（WheelUp/Down/Left/Right）→ dy/dx = ±1 格
//     （Unity 契约「dy 单位 = 格，core 每 100 design px/格」），帧内累积合并。
//   - 触摸/IME composition：Godot runtime IME 组字串无公开 API——composition 通道暂不
//     接（系统 IME 对非 IME-aware 窗口走兼容路径，上屏字符经 Unicode 进 textinput）；
//     触摸（ScreenTouch/Drag）后续批（移动端输入 #70 邻域）。

using System.Collections.Generic;
using Godot;
using Yio.Bindings;

namespace Yio
{
    public sealed unsafe class GodotInputCollector
    {
        readonly List<PointerEvent> _pointerEdge = new();
        readonly List<KeyEvent> _keys = new();
        readonly List<uint> _text = new();
        Vector2 _mousePos;                 // 指针位置（design 坐标——入缓冲时已映射）
        float _wheelX, _wheelY;

        // screen→design 适配映射三元组（单源 = Rust yio_compute_adaptation，driver 每次适配
        // 重算后注入）。默认恒等（窗口=设计分辨率 1:1）。Godot viewport 坐标 top-left
        // y-down 与 design 同向——无 Unity 的 screenH-y 翻转项。
        internal float MapScale = 1f;
        internal float MapOffX;
        internal float MapOffY;

        Vector2 ScreenToDesign(Vector2 screen)
        {
            float s = MapScale > 0 ? MapScale : 1f;
            return new Vector2((screen.X - MapOffX) / s, (screen.Y - MapOffY) / s);
        }

        /// <summary>Driver._Input 转发入口（帧内可多次，CollectInput 消费清空）。</summary>
        public void OnInputEvent(InputEvent e)
        {
            if (e is InputEventMouseButton mb)
            {
                var d = ScreenToDesign(mb.Position);
                _mousePos = d;
                switch (mb.ButtonIndex)
                {
                    case MouseButton.Left:
                    case MouseButton.Middle:
                    case MouseButton.Right:
                        // 单 slot 模型对齐 Unity：次键在另一键按住期间的转变降级丢弃
                        //（防二次 Down 重置 core down_pos/drag 状态）——首帧简化：边沿
                        // 逐条转发（Godot 帧内多事件少见，快速连点场景多条语义更准）。
                        _pointerEdge.Add(new PointerEvent
                        {
                            kind = mb.Pressed ? (byte)0 : (byte)1,
                            button = (byte)(mb.ButtonIndex == MouseButton.Left ? 0
                                : mb.ButtonIndex == MouseButton.Middle ? 1 : 2),
                            pad0 = 0, pad1 = 0, touch_id = -1,
                            x = _mousePos.X, y = _mousePos.Y,
                        });
                        break;
                    case MouseButton.WheelUp: _wheelY += 1f; break;
                    case MouseButton.WheelDown: _wheelY -= 1f; break;
                    case MouseButton.WheelLeft: _wheelX -= 1f; break;
                    case MouseButton.WheelRight: _wheelX += 1f; break;
                }
            }
            else if (e is InputEventMouseMotion mm)
            {
                _mousePos = ScreenToDesign(mm.Position);
            }
            else if (e is InputEventKey k)
            {
                uint code = MapKey(k.Keycode);
                if (code != 0)
                {
                    _keys.Add(new KeyEvent
                    {
                        key_code = code,
                        modifiers = CurrentModifiers(),
                        // echo = OS 自动重复（Unity 需 KeyRepeatState 合成——Godot 系统自带，
                        // 仅 keydown 通道重复，字符通道不重复——对齐 Unity 语义）。
                        is_down = k.Pressed,
                        pad0 = 0, pad1 = 0,
                    });
                }
                // 字符通道：keydown 的可打印字符（非 echo——长按不重复插字）。
                if (k.Pressed && !k.Echo && k.Unicode != 0)
                    _text.Add((uint)k.Unicode);
            }
        }

        /// <summary>帧消费（backend.CollectInput 调；Unity 版 Collect/CollectKeys/CollectText/
        /// CollectWheel 四段合一——Godot 事件已分类入各自缓冲）。</summary>
        public void Collect(System.IntPtr stage)
        {
            if (stage == System.IntPtr.Zero) return;
            StageHandle* h = (StageHandle*)stage.ToPointer();

            // 指针：边沿事件优先逐条发；无边沿帧发一条 Move（hover 状态机依赖每帧位置）。
            if (_pointerEdge.Count > 0)
            {
                var arr = _pointerEdge.ToArray();
                fixed (PointerEvent* p = arr)
                    Native.yio_stage_set_input(h, p, (nuint)arr.Length);
                _pointerEdge.Clear();
            }
            else
            {
                var ev = new PointerEvent
                {
                    kind = 2, button = 0, pad0 = 0, pad1 = 0, touch_id = -1,
                    x = _mousePos.X, y = _mousePos.Y,
                };
                Native.yio_stage_set_input(h, &ev, 1);
            }

            // 键盘（帧内多条一次发；无事件发空——core 侧无键盘输入）。
            if (_keys.Count > 0)
            {
                var arr = _keys.ToArray();
                fixed (KeyEvent* p = arr)
                    Native.yio_stage_set_key_input(h, p, (nuint)arr.Length);
                _keys.Clear();
            }
            else
            {
                Native.yio_stage_set_key_input(h, null, 0);
            }

            // 文本（UTF-32 码点）。
            if (_text.Count > 0)
            {
                var arr = _text.ToArray();
                fixed (uint* p = arr)
                    Native.yio_stage_set_text_input(h, p, (nuint)arr.Length);
                _text.Clear();
            }

            // 滚轮（帧内累积合并；notch 单位——core 每 100 design px/格）。
            if (_wheelX != 0f || _wheelY != 0f)
            {
                var ev = new WheelEvent { x = _mousePos.X, y = _mousePos.Y, delta_x = _wheelX, delta_y = _wheelY };
                Native.yio_stage_set_wheel_input(h, &ev, 1);
                _wheelX = _wheelY = 0f;
            }
        }

        static byte CurrentModifiers()
        {
            byte m = 0;
            if (Input.IsKeyPressed(Godot.Key.Shift)) m |= 0x01;
            if (Input.IsKeyPressed(Godot.Key.Ctrl)) m |= 0x02;
            if (Input.IsKeyPressed(Godot.Key.Alt)) m |= 0x04;
            return m;
        }

        // Godot.Key → YioKeyCode 数值族（GLFW 同源；Yio.Types.cs 契约表——加键先加契约表再补此映射）。
        static uint MapKey(Godot.Key k) => k switch
        {
            Godot.Key.Enter or Godot.Key.KpEnter => 13,
            Godot.Key.Escape => 27,
            Godot.Key.Tab => 9,
            Godot.Key.Space => 32,
            Godot.Key.Backspace => 8,
            Godot.Key.Delete => 127,
            Godot.Key.Left => 276,
            Godot.Key.Right => 275,
            Godot.Key.Up => 273,
            Godot.Key.Down => 274,
            Godot.Key.Home => 278,
            Godot.Key.End => 279,
            Godot.Key.A => 97, Godot.Key.B => 98, Godot.Key.C => 99, Godot.Key.D => 100,
            Godot.Key.E => 101, Godot.Key.F => 102, Godot.Key.G => 103, Godot.Key.H => 104,
            Godot.Key.I => 105, Godot.Key.J => 106, Godot.Key.K => 107, Godot.Key.L => 108,
            Godot.Key.M => 109, Godot.Key.N => 110, Godot.Key.O => 111, Godot.Key.P => 112,
            Godot.Key.Q => 113, Godot.Key.R => 114, Godot.Key.S => 115, Godot.Key.T => 116,
            Godot.Key.U => 117, Godot.Key.V => 118, Godot.Key.W => 119, Godot.Key.X => 120,
            Godot.Key.Y => 121, Godot.Key.Z => 122,
            Godot.Key.Key0 => 48, Godot.Key.Key1 => 49, Godot.Key.Key2 => 50, Godot.Key.Key3 => 51,
            Godot.Key.Key4 => 52, Godot.Key.Key5 => 53, Godot.Key.Key6 => 54, Godot.Key.Key7 => 55,
            Godot.Key.Key8 => 56, Godot.Key.Key9 => 57,
            Godot.Key.F1 => 282, Godot.Key.F2 => 283, Godot.Key.F3 => 284, Godot.Key.F4 => 285,
            Godot.Key.F5 => 286, Godot.Key.F6 => 287, Godot.Key.F7 => 288, Godot.Key.F8 => 289,
            Godot.Key.F9 => 290, Godot.Key.F10 => 291, Godot.Key.F11 => 292, Godot.Key.F12 => 293,
            _ => 0,
        };
    }
}

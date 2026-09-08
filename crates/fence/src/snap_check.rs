//! scroll-snap 声明完整性检查（#59，warning）。吸附是容器能力：`scroll-snap-type`
//! 挂滚动容器、子元素 `scroll-snap-align` 提供吸附点。两种「声明了却不生效」的
//! 形态在此显式化（同一诊断码 [`DiagnosticCode::FenceSnapNoEffect`]）：
//!
//! - **W1**：snap-type 声明在 snap 轴无 `overflow:scroll/auto` 的元素上——吸附
//!   语义挂滚动容器，非滚动容器声明是无效能力。
//! - **W2**：snap 容器的直接子元素全部未声明 `scroll-snap-align`（CSS 初始值
//!   `none` = 不提供吸附点，照浏览器——无吸附点则不吸附）。AI 最常见漏写形态：
//!   容器写了 snap-type、页子忘了 align，浏览器预览同样不吸附（预览不说谎），
//!   但「不吸附」应当显式而非静默。
//!
//! warning 而非 error：两种形态与浏览器行为一致（浏览器同样静默不吸附），无
//! 预览↔运行时分歧；提示只为把静默失效变成可见事实。
//!
//! 视野边界（paint_order_check 同款）：inline resolve 产物（styles）+ 结构匹配
//! 的 class 规则原文，保守合并「可能生效」方向；运行时可变属性选择器规则跳过。

use crate::diagnostic::{Diagnostic, DiagnosticCode, LineMap};
use crate::ir::{IrNodeKind, IrTree};
use yio_core::style::dynamic::DynamicRule;
use yio_core::style::resolved::{OverflowMode, ResolvedStyle, SnapAlign, SnapTypeMode};

/// 元素的静态可见 snap 事实（inline resolve + class 规则保守合并）。
#[derive(Debug, Clone, Copy)]
struct SnapFlags {
    /// 声明的 snap-type（None = 未声明）。
    snap_type: SnapTypeMode,
    /// 子元素里是否存在 align 声明。
    child_align_declared: bool,
    /// snap 轴 overflow 是否 ∈ {scroll, auto}（capable）。
    snap_axis_scrollable: bool,
}

/// 计算容器侧声明：snap-type + snap 轴 overflow。class 规则任一匹配规则声明
/// 即计（保守方向：可能生效就当生效——W1/W2 是 warning，少误报优先）。
fn snap_flags(
    tree: &IrTree,
    styles: &[ResolvedStyle],
    rules: &[DynamicRule],
    idx: usize,
) -> SnapFlags {
    let s = &styles[idx];
    let mut snap_type = s.scroll_snap_type;
    // 两轴「可能 capable」无序收集（规则声明顺序与 cascade 无关——保守方向：
    // 任一来源声明 scroll/auto 即计，防 overflow 先于 snap-type 处理时漏计误报）。
    let mut x_capable = capable(s.overflow_x);
    let mut y_capable = capable(s.overflow_y);
    for r in rules {
        if selector_has_runtime_mutable_attr(r)
            || !crate::control_css_check::selector_matches_node(&r.selector, tree, idx)
        {
            continue;
        }
        for d in &r.declarations {
            match d.prop.as_str() {
                "scroll-snap-type" => {
                    if let Some(m) = yio_core::style::mapping::parse_scroll_snap_type(&d.value) {
                        // 首个非 none 声明定型（多规则互斥不猜特异性，保守取有声明）。
                        if m != SnapTypeMode::None && snap_type == SnapTypeMode::None {
                            snap_type = m;
                        }
                    }
                }
                "overflow-x" if capable(parse_overflow_decl(&d.value)) => x_capable = true,
                "overflow-y" if capable(parse_overflow_decl(&d.value)) => y_capable = true,
                _ => {}
            }
        }
    }
    let snap_axis_scrollable = match snap_type {
        SnapTypeMode::XMandatory => x_capable,
        SnapTypeMode::YMandatory => y_capable,
        SnapTypeMode::None => false,
    };
    // 子侧：任一直接子元素（inline 或匹配规则）声明 align != none。
    let mut child_align_declared = false;
    for &c in &tree.nodes[idx].children {
        let node = &tree.nodes[c.0];
        if !matches!(node.kind, IrNodeKind::Element(_)) || styles.get(c.0).is_none() {
            continue;
        }
        if styles[c.0].scroll_snap_align != SnapAlign::None {
            child_align_declared = true;
            break;
        }
        let from_rule = rules.iter().any(|r| {
            !selector_has_runtime_mutable_attr(r)
                && crate::control_css_check::selector_matches_node(&r.selector, tree, c.0)
                && r.declarations.iter().any(|d| {
                    d.prop == "scroll-snap-align"
                        && yio_core::style::mapping::parse_scroll_snap_align(&d.value)
                            .is_some_and(|a| a != SnapAlign::None)
                })
        });
        if from_rule {
            child_align_declared = true;
            break;
        }
    }
    SnapFlags {
        snap_type,
        child_align_declared,
        snap_axis_scrollable,
    }
}

fn capable(m: OverflowMode) -> bool {
    matches!(m, OverflowMode::Scroll | OverflowMode::Auto)
}

fn parse_overflow_decl(v: &str) -> OverflowMode {
    match v.trim() {
        "scroll" => OverflowMode::Scroll,
        "auto" => OverflowMode::Auto,
        "hidden" => OverflowMode::Hidden,
        _ => OverflowMode::Visible,
    }
}

/// 规则选择器含运行时可变属性（hidden/aria-*）→ 跳过（paint_order_check 同口径）。
fn selector_has_runtime_mutable_attr(rule: &DynamicRule) -> bool {
    use crate::inline_context_check::RUNTIME_MUTABLE_ATTRS;
    rule.selector.compound.iter().any(|c| {
        c.attrs
            .iter()
            .any(|a| RUNTIME_MUTABLE_ATTRS.contains(&a.name.as_str()))
    })
}

/// 入参形状与 [`crate::paint_order_check::check_paint_order`] 同构。
pub fn check_snap(
    tree: &IrTree,
    styles: &[ResolvedStyle],
    rules: &[DynamicRule],
    file: &str,
    line_map: &LineMap,
) -> Vec<Diagnostic> {
    let mut diags = Vec::new();
    for (idx, node) in tree.nodes.iter().enumerate() {
        if !matches!(node.kind, IrNodeKind::Element(_)) || styles.get(idx).is_none() {
            continue;
        }
        let f = snap_flags(tree, styles, rules, idx);
        if f.snap_type == SnapTypeMode::None {
            continue;
        }
        let loc = line_map.source_location(node.span.start, file.to_string());
        if !f.snap_axis_scrollable {
            diags.push(Diagnostic::warning(
                DiagnosticCode::FenceSnapNoEffect,
                "scroll-snap-type declared on an element whose snap axis has no \
                     overflow:scroll/auto — snapping only applies to scroll containers. \
                     The declaration is inert (matching browser behavior, no snap \
                     happens); add overflow (e.g. `overflow-x:scroll`) or move \
                     scroll-snap-type to the scroll container."
                    .to_string(),
                loc,
            ));
        } else if !f.child_align_declared {
            diags.push(Diagnostic::warning(
                DiagnosticCode::FenceSnapNoEffect,
                "scroll-snap-type declared but no direct child declares \
                     scroll-snap-align — children default to `none` (no snap area), \
                     so the container will not snap (matching browser behavior). \
                     Add `scroll-snap-align:start|center|end` to the page children."
                    .to_string(),
                loc,
            ));
        }
    }
    diags
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::diagnostic::Severity;
    use crate::pipeline::parse_template;

    fn has(result: &crate::pipeline::ParsedTemplate, code: DiagnosticCode, sev: Severity) -> bool {
        result
            .diagnostics
            .iter()
            .any(|d| d.code == code && d.severity == sev)
    }

    /// W1：snap 轴无 overflow 的元素声明 snap-type → warning。
    #[test]
    fn w1_snap_without_overflow_warns() {
        let html = r#"<div style="scroll-snap-type:x mandatory">
  <div style="scroll-snap-align:start"></div>
</div>"#;
        let r = parse_template(html, "t.html");
        assert!(
            has(&r, DiagnosticCode::FenceSnapNoEffect, Severity::Warning),
            "无 overflow 的 snap-type 应 warning: {:?}",
            r.diagnostics
        );
    }

    /// W2：snap 容器无任何子声明 align → warning。
    #[test]
    fn w2_no_aligned_children_warns() {
        let html = r#"<div style="overflow-x:scroll;scroll-snap-type:x mandatory">
  <div></div>
</div>"#;
        let r = parse_template(html, "t.html");
        assert!(
            has(&r, DiagnosticCode::FenceSnapNoEffect, Severity::Warning),
            "无 align 子应 warning: {:?}",
            r.diagnostics
        );
    }

    /// 健康 banner（overflow + snap-type + align 子）→ 无 FenceSnapNoEffect。
    #[test]
    fn healthy_banner_no_warning() {
        let html = r#"<div style="overflow-x:scroll;scroll-snap-type:x mandatory">
  <div style="scroll-snap-align:start"></div>
  <div style="scroll-snap-align:start"></div>
</div>"#;
        let r = parse_template(html, "t.html");
        assert!(
            !has(&r, DiagnosticCode::FenceSnapNoEffect, Severity::Warning),
            "健康 banner 不该警告: {:?}",
            r.diagnostics
        );
    }

    /// class 规则侧声明同样计入（结构匹配的 .pages 规则声明 overflow/snap-type）。
    #[test]
    fn class_rule_declarations_counted() {
        let html = r#"<style>
.carousel { overflow-x: scroll; scroll-snap-type: x mandatory; }
.page { scroll-snap-align: start; }
</style>
<div class="carousel"><div class="page"></div></div>"#;
        let r = parse_template(html, "t.html");
        assert!(
            !has(&r, DiagnosticCode::FenceSnapNoEffect, Severity::Warning),
            "class 规则声明的 banner 不该警告: {:?}",
            r.diagnostics
        );
    }
}

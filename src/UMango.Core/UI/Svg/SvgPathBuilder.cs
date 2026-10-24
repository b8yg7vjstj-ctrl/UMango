// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgPathGeometryParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. **剥离 WPF 依赖**：产出平台中立的 SvgPathGeometry，而非 WPF Geometry；
//   3. 原版的圆弧在 rx/ry 为 0 时退化成直线，UMango 保持同样口径。

namespace UMango.Core.UI.Svg;

/// <summary>
/// 把 SVG 路径数据执行成平台中立的路径模型。
///
/// 语义严格按 SVG 1.1 规范：
///   · 相对指令（���写）基于「当前点」偏移；
///   · <c>M/m</c> 之后的坐标对按 <c>L/l</c> 处理；
///   · <c>S/s</c>（光滑三次）与 <c>T/t</c>（光滑二次）沿用上一段的控制点镜像；
///   · <c>Z/z</c> 闭合当前子路径并把当前点移回起点。
/// </summary>
public static class SvgPathBuilder
{
    /// <summary>解析路径数据。</summary>
    /// <exception cref="FormatException">路径数据非法时抛出。</exception>
    public static SvgPathGeometry Build(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var tokens = SvgPathTokenizer.Tokenize(data);
        var geometry = new SvgPathGeometry();

        var index = 0;
        var current = new SvgPoint(0, 0);
        var figureStart = current;
        SvgPathFigure? figure = null;
        var figureOpen = false;
        var lastCommand = '\0';
        SvgPoint? lastCubicControl = null;
        SvgPoint? lastQuadraticControl = null;
        char command = '\0';

        while (index < tokens.Count)
        {
            if (tokens[index].Kind == SvgPathTokenKind.Command)
            {
                command = tokens[index].Command;
                index++;
            }
            else if (command == '\0')
            {
                throw new FormatException("SVG path data must start with a command.");
            }

            switch (command)
            {
                case 'M':
                case 'm':
                {
                    var relative = command == 'm';
                    if (!HasNumber(tokens, index)) break;
                    var first = ReadPoint(tokens, ref index, relative, current);
                    figure = new SvgPathFigure { StartPoint = first };
                    geometry.Figures.Add(figure);
                    current = first;
                    figureStart = first;
                    figureOpen = true;
                    lastCubicControl = null;
                    lastQuadraticControl = null;
                    lastCommand = 'M';
                    // SVG 规范：M/m 后续坐标对等同 L/l。
                    while (HasNumber(tokens, index))
                    {
                        var point = ReadPoint(tokens, ref index, relative, current);
                        AddLine(ref figure, ref figureOpen, ref current, point, geometry);
                    }

                    break;
                }

                case 'L':
                case 'l':
                {
                    var relative = command == 'l';
                    while (HasNumber(tokens, index))
                    {
                        var point = ReadPoint(tokens, ref index, relative, current);
                        AddLine(ref figure, ref figureOpen, ref current, point, geometry);
                    }

                    SetLastCommand(ref lastCommand, ref lastCubicControl, ref lastQuadraticControl, 'L');
                    break;
                }

                case 'H':
                case 'h':
                {
                    var relative = command == 'h';
                    while (HasNumber(tokens, index))
                    {
                        var x = ReadNumber(tokens, ref index);
                        var point = relative ? new SvgPoint(current.X + x, current.Y) : new SvgPoint(x, current.Y);
                        AddLine(ref figure, ref figureOpen, ref current, point, geometry);
                    }

                    SetLastCommand(ref lastCommand, ref lastCubicControl, ref lastQuadraticControl, 'H');
                    break;
                }

                case 'V':
                case 'v':
                {
                    var relative = command == 'v';
                    while (HasNumber(tokens, index))
                    {
                        var y = ReadNumber(tokens, ref index);
                        var point = relative ? new SvgPoint(current.X, current.Y + y) : new SvgPoint(current.X, y);
                        AddLine(ref figure, ref figureOpen, ref current, point, geometry);
                    }

                    SetLastCommand(ref lastCommand, ref lastCubicControl, ref lastQuadraticControl, 'V');
                    break;
                }

                case 'C':
                case 'c':
                {
                    var relative = command == 'c';
                    while (HasNumber(tokens, index))
                    {
                        var control1 = ReadPoint(tokens, ref index, relative, current);
                        var control2 = ReadPoint(tokens, ref index, relative, current);
                        var end = ReadPoint(tokens, ref index, relative, current);
                        EnsureFigure(ref figure, ref figureOpen, current, geometry);
                        figure!.Segments.Add(new SvgPathSegment
                        {
                            Kind = SvgSegmentKind.CubicBezier,
                            Control1 = control1,
                            Control2 = control2,
                            EndPoint = end,
                        });
                        current = end;
                        lastCubicControl = control2;
                        lastQuadraticControl = null;
                        lastCommand = 'C';
                    }

                    break;
                }

                case 'S':
                case 's':
                {
                    var relative = command == 's';
                    while (HasNumber(tokens, index))
                    {
                        var control1 = lastCommand is 'C' or 'S' && lastCubicControl is not null
                            ? Reflect(lastCubicControl.Value, current)
                            : current;
                        var control2 = ReadPoint(tokens, ref index, relative, current);
                        var end = ReadPoint(tokens, ref index, relative, current);
                        EnsureFigure(ref figure, ref figureOpen, current, geometry);
                        figure!.Segments.Add(new SvgPathSegment
                        {
                            Kind = SvgSegmentKind.CubicBezier,
                            Control1 = control1,
                            Control2 = control2,
                            EndPoint = end,
                        });
                        current = end;
                        lastCubicControl = control2;
                        lastQuadraticControl = null;
                        lastCommand = 'S';
                    }

                    break;
                }

                case 'Q':
                case 'q':
                {
                    var relative = command == 'q';
                    while (HasNumber(tokens, index))
                    {
                        var control = ReadPoint(tokens, ref index, relative, current);
                        var end = ReadPoint(tokens, ref index, relative, current);
                        EnsureFigure(ref figure, ref figureOpen, current, geometry);
                        figure!.Segments.Add(new SvgPathSegment
                        {
                            Kind = SvgSegmentKind.QuadraticBezier,
                            Control1 = control,
                            EndPoint = end,
                        });
                        current = end;
                        lastQuadraticControl = control;
                        lastCubicControl = null;
                        lastCommand = 'Q';
                    }

                    break;
                }

                case 'T':
                case 't':
                {
                    var relative = command == 't';
                    while (HasNumber(tokens, index))
                    {
                        var control = lastCommand is 'Q' or 'T' && lastQuadraticControl is not null
                            ? Reflect(lastQuadraticControl.Value, current)
                            : current;
                        var end = ReadPoint(tokens, ref index, relative, current);
                        EnsureFigure(ref figure, ref figureOpen, current, geometry);
                        figure!.Segments.Add(new SvgPathSegment
                        {
                            Kind = SvgSegmentKind.QuadraticBezier,
                            Control1 = control,
                            EndPoint = end,
                        });
                        current = end;
                        lastQuadraticControl = control;
                        lastCubicControl = null;
                        lastCommand = 'T';
                    }

                    break;
                }

                case 'A':
                case 'a':
                {
                    var relative = command == 'a';
                    while (HasNumber(tokens, index))
                    {
                        var rx = Math.Abs(ReadNumber(tokens, ref index));
                        var ry = Math.Abs(ReadNumber(tokens, ref index));
                        var rotation = ReadNumber(tokens, ref index);
                        var isLargeArc = Math.Abs(ReadNumber(tokens, ref index)) > 0D;
                        var isClockwise = Math.Abs(ReadNumber(tokens, ref index)) > 0D;
                        var end = ReadPoint(tokens, ref index, relative, current);
                        EnsureFigure(ref figure, ref figureOpen, current, geometry);
                        if (rx <= 0D || ry <= 0D)
                        {
                            // 规范：半径为 0 时按直线处理。
                            figure!.Segments.Add(new SvgPathSegment
                            {
                                Kind = SvgSegmentKind.Line,
                                EndPoint = end,
                            });
                        }
                        else
                        {
                            figure!.Segments.Add(new SvgPathSegment
                            {
                                Kind = SvgSegmentKind.Arc,
                                RadiusX = rx,
                                RadiusY = ry,
                                Rotation = rotation,
                                IsLargeArc = isLargeArc,
                                IsClockwise = isClockwise,
                                EndPoint = end,
                            });
                        }

                        current = end;
                        lastCubicControl = null;
                        lastQuadraticControl = null;
                        lastCommand = 'A';
                    }

                    break;
                }

                case 'Z':
                case 'z':
                {
                    if (figureOpen && figure is not null)
                    {
                        figure.IsClosed = true;
                        current = figureStart;
                        figureOpen = false;
                    }

                    SetLastCommand(ref lastCommand, ref lastCubicControl, ref lastQuadraticControl, 'Z');
                    break;
                }

                default:
                    throw new FormatException($"Unsupported SVG path command: {command}");
            }
        }

        return geometry;
    }

    private static void AddLine(
        ref SvgPathFigure? figure,
        ref bool figureOpen,
        ref SvgPoint current,
        SvgPoint point,
        SvgPathGeometry geometry)
    {
        EnsureFigure(ref figure, ref figureOpen, current, geometry);
        figure!.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = point,
        });
        current = point;
    }

    private static void EnsureFigure(
        ref SvgPathFigure? figure,
        ref bool figureOpen,
        SvgPoint current,
        SvgPathGeometry geometry)
    {
        if (figureOpen && figure is not null)
            return;

        figure = new SvgPathFigure { StartPoint = current };
        geometry.Figures.Add(figure);
        figureOpen = true;
    }

    private static void SetLastCommand(
        ref char lastCommand,
        ref SvgPoint? lastCubicControl,
        ref SvgPoint? lastQuadraticControl,
        char command)
    {
        lastCommand = command;
        if (command is not ('C' or 'S'))
            lastCubicControl = null;
        if (command is not ('Q' or 'T'))
            lastQuadraticControl = null;
    }

    private static SvgPoint Reflect(SvgPoint point, SvgPoint around)
        => new(around.X * 2D - point.X, around.Y * 2D - point.Y);

    private static bool HasNumber(IReadOnlyList<SvgPathToken> tokens, int index)
        => index < tokens.Count && tokens[index].Kind == SvgPathTokenKind.Number;

    private static double ReadNumber(IReadOnlyList<SvgPathToken> tokens, ref int index)
    {
        if (!HasNumber(tokens, index))
            throw new FormatException("Expected number in SVG path data.");
        return tokens[index++].Number;
    }

    private static SvgPoint ReadPoint(
        IReadOnlyList<SvgPathToken> tokens,
        ref int index,
        bool relative,
        SvgPoint current)
    {
        var x = ReadNumber(tokens, ref index);
        var y = ReadNumber(tokens, ref index);
        return relative ? new SvgPoint(current.X + x, current.Y + y) : new SvgPoint(x, y);
    }
}

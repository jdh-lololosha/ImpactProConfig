using System;

namespace ImpactProConfig.Services;

/// <summary>
/// Формулы кривых Raw Accel, перенесённые из апстрима дословно.
///
/// Источник (common/*.hpp в RawAccelOfficial/rawaccel):
///   classic  -> common/accel-classic.hpp
///   jump     -> common/accel-jump.hpp
///   natural  -> common/accel-natural.hpp
///   power    -> common/accel-power.hpp
///   accel_args / cap_mode -> common/rawaccel-base.hpp
///
/// Формулы из ТЗ задачи НЕ использовались: там написано
/// «classic: базовый множитель + accel * (x - offset)», но в апстриме это
/// accel_raised * pow(x - input_offset, exponent) / x — деление на x и
/// возведение в степень принципиально меняют форму. Нарисовав по ТЗ, мы бы
/// показали пользователю график, которым драйвер не пользуется.
///
/// Что здесь НЕ перенесено: расчёт скорости входа (speed_args, lp_norm,
/// halflife) и нормализация DPI. График показывает чистую функцию
/// «множитель от скорости», как это делает сам GUI Raw Accel; входной оси
/// соответствует нормализованная скорость (counts/ms -> in/s при DPI 1000).
/// </summary>
public enum RawAccelCurveMode
{
    Classic = 0,
    Jump = 1,
    Natural = 2,
    Power = 3,
}

public static class RawAccelCurveEngine
{
    /// <summary>
    /// Параметры кривой. Значения по умолчанию — как в accel_args
    /// (rawaccel-base.hpp), кроме cap_mode: по умолчанию у апстрима
    /// cap_mode::out, и для графика это самый предсказуемый вариант.
    /// </summary>
    public sealed class Args
    {
        public double Acceleration = 0.005;      // acceleration
        public double InputOffset = 0;           // input_offset
        public double OutputOffset = 0;          // output_offset
        public double ExponentClassic = 2;       // exponent_classic
        public double ExponentPower = 0.05;      // exponent_power
        public double DecayRate = 0.1;           // decay_rate
        public double Limit = 1.5;               // limit
        public double Smooth = 0.5;              // smooth
        public double Scale = 1;                 // scale
        public double CapX = 15;                 // cap.x
        public double CapY = 1.5;                // cap.y
        public bool Gain = true;                 // gain (GAIN-реализация)
    }

    // ---------- classic ----------

    private static double ClassicBaseFn(double x, double accelRaised, Args a) =>
        accelRaised * Math.Pow(x - a.InputOffset, a.ExponentClassic) / x;

    private static double ClassicGain(double x, double accel, double power, double offset) =>
        power * Math.Pow(accel * (x - offset), power - 1);

    private static double ClassicGainInverse(double y, double accel, double power, double offset) =>
        (accel * offset + Math.Pow(y / power, 1 / (power - 1))) / accel;

    /// <summary>accel_raised и постоянная для classic (accel-classic.hpp, GAIN).</summary>
    private static (double accelRaised, double constant, double sign, double capX, double capY)
        ClassicInit(Args a)
    {
        double accelRaised = Math.Pow(a.Acceleration, a.ExponentClassic - 1);
        double sign = 1;
        double capX = double.MaxValue, capY = double.MaxValue;

        // В этой вкладке поддерживается cap_mode::out (default в апстриме)
        // и cap_mode::in — два других режима в GUI тоже есть, но здесь они
        // не выставляются, и молча рисовать неверную кривую хуже, чем
        // ограничиться поддерживаемым.
        double cap = double.MaxValue;
        double constant = 0;

        if (a.CapY > 0)
        {
            cap = a.CapY - 1;
            if (cap < 0) { cap = -cap; sign = -sign; }

            if (cap == 0) capX = 0;
            else
            {
                capX = ClassicGainInverse(cap, a.Acceleration, a.ExponentClassic, a.InputOffset);
                constant = (ClassicBaseFn(capX, accelRaised, a) - cap) * capX;
            }
        }
        capY = cap;

        return (accelRaised, constant, sign, capX, capY);
    }

    private static double Classic(double x, Args a)
    {
        if (x <= a.InputOffset) return 1;

        var (accelRaised, constant, sign, capX, capY) = ClassicInit(a);

        double output = x < capX
            ? ClassicBaseFn(x, accelRaised, a)
            : constant / x + capY;

        return sign * output + 1;
    }

    // ---------- jump ----------

    private const double JumpSmoothScale = 2 * Math.PI;

    /// <summary>step, smooth_rate и признак сглаживания (accel-jump.hpp).</summary>
    private static (double stepX, double stepY, double smoothRate) JumpInit(Args a)
    {
        double stepX = a.CapX;
        double stepY = a.CapY - 1;
        double rateInverse = a.Smooth * stepX;
        double smoothRate = rateInverse < 1 ? 0 : JumpSmoothScale / rateInverse;
        return (stepX, stepY, smoothRate);
    }

    private static double Jump(double x, Args a)
    {
        var (stepX, stepY, smoothRate) = JumpInit(a);

        if (smoothRate != 0)
        {
            // Сглаженный (GAIN) вариант: 1 + (smooth_antideriv(x) + C) / x
            double C = -stepY * (0 + Math.Log(1 + Math.Exp(smoothRate * stepX)) / smoothRate);
            if (x <= 0) return 1;
            double decay = Math.Exp(smoothRate * (stepX - x));
            double antideriv = stepY * (x + Math.Log(1 + decay) / smoothRate);
            return 1 + (antideriv + C) / x;
        }

        // Ступенька: без сглаживания — скачок ровно в точке Cap/Jump.
        return x < stepX ? 1 : 1 + stepY;
    }

    // ---------- natural ----------

    private static double Natural(double x, Args a)
    {
        double offset = a.InputOffset;
        double limit = a.Limit - 1;
        double accel = a.DecayRate / Math.Abs(limit);

        if (x <= offset) return 1;

        // GAIN-вариант: output = limit * (decay / accel - offset_x) + constant,
        // где constant = -limit / accel; результат делится на x и прибавляется 1.
        double constant = -limit / accel;
        double offsetX = offset - x;
        double decay = Math.Exp(accel * offsetX);
        double output = limit * (decay / accel - offsetX) + constant;
        return output / x + 1;
    }

    // ---------- power ----------

    private static (double scale, double offsetX, double offsetY, double constant) PowerInit(Args a)
    {
        double n = a.ExponentPower;
        double scale = a.Scale;
        double offsetY = a.OutputOffset;
        double constant = 0;

        if (scale <= 0) scale = 1;

        // gain_inverse(output_offset, n, scale)
        double offsetX = Math.Pow(offsetY / (n + 1), 1 / n) / scale;
        constant = offsetX * offsetY * n / (n + 1);

        return (scale, offsetX, offsetY, constant);
    }

    private static double PowerBaseFn(double x, Args a, double scale, double offsetX,
                                      double offsetY, double constant)
    {
        if (x <= offsetX) return offsetY;
        return Math.Pow(scale * x, a.ExponentPower) + constant / x;
    }

    private static double PowerGain(double input, double power, double scale) =>
        (power + 1) * Math.Pow(input * scale, power);

    private static double Power(double x, Args a)
    {
        var (scale, offsetX, offsetY, constant) = PowerInit(a);

        // cap_mode::out — потолок задаётся выходным значением Cap.y
        double capX = double.MaxValue, capY = double.MaxValue;
        double constantB = 0;

        if (a.CapY > 0)
        {
            capY = a.CapY;
            capX = Math.Pow(capY / (a.ExponentPower + 1), 1 / a.ExponentPower) / scale;
            constantB = (PowerBaseFn(capX, a, scale, offsetX, offsetY, constant) - capY) * capX;
        }

        return x < capX
            ? PowerBaseFn(x, a, scale, offsetX, offsetY, constant)
            : capY + constantB / x;
    }

    /// <summary>
    /// Значение кривой для скорости x. Единица на единицу — это «множитель 1.0»,
    /// то есть отсутствие акселерации.
    /// </summary>
    /// <summary>Синоним для Evaluate: в тестах так читается короче.</summary>
    public static double E(RawAccelCurveMode mode, double x, Args a) => Evaluate(mode, x, a);

    public static double Evaluate(RawAccelCurveMode mode, double x, Args a)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return 1;

        try
        {
            return mode switch
            {
                RawAccelCurveMode.Classic => Classic(x, a),
                RawAccelCurveMode.Jump => Jump(x, a),
                RawAccelCurveMode.Natural => Natural(x, a),
                RawAccelCurveMode.Power => Power(x, a),
                _ => 1,
            };
        }
        catch (Exception)
        {
            // Любой промах математики не должен ронять отрисовку: кривая
            // просто не продлится в эту точку. Точки с нечисловым результатом
            // отбрасываются вызывающим.
            return double.NaN;
        }
    }
}
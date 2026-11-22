using System.ComponentModel.DataAnnotations;

namespace AnalyticsService.Validation;

[AttributeUsage(AttributeTargets.Property)]
public sealed class NotDefaultAttribute : ValidationAttribute
{
    public NotDefaultAttribute() : base("The {0} field must not have its default value.") { }

    public override bool IsValid(object? value) =>
        value is ValueType && !value.Equals(Activator.CreateInstance(value.GetType()));
}

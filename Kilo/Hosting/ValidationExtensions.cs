using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace Kilo.Hosting;

public static class ValidationExtensions
{
    public static ActionResult RequestValidationProblem(this ControllerBase controller, ValidationResult result)
    {
        foreach (var error in result.Errors)
        {
            controller.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return controller.ValidationProblem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            instance: controller.HttpContext.Request.Path);
    }
}

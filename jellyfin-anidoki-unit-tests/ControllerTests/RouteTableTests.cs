using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using jellyfin_anidoki;
using jellyfin_anidoki_unit_tests.Characterization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing.Template;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

/// <summary>
/// Pins every route the plugin serves: verb, full template, effective authorization, action, parameters,
/// <c>[ApiController]</c>, order and inbound precedence. Controller names are left out on purpose, so moving an
/// action to another controller leaves the table unchanged while any change to its URL, verb or policy shows.
/// </summary>
[TestFixture]
public class RouteTableTests {
    [Test]
    public void TheRouteTableMatchesItsApprovedFile() {
        var lines = Routes()
            .OrderBy(route => route.Template, StringComparer.Ordinal)
            .ThenBy(route => route.Verb, StringComparer.Ordinal)
            .Select(route => route.Line);
        ApprovedFile.Verify("routes", string.Join("\n", lines) + "\n");
    }

    [Test]
    public void NoVerbAndTemplateIsServedTwice() {
        var duplicates = Routes()
            .GroupBy(route => (route.Verb, Template: route.Template.ToLowerInvariant()))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Verb} {group.Key.Template}")
            .ToArray();
        Assert.That(duplicates, Is.Empty);
    }

    private sealed record Route(string Verb, string Template, string Line);

    private static IEnumerable<Route> Routes() {
        var controllers = typeof(Plugin).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);
        foreach (var controller in controllers) {
            var classRoutes = controller.GetCustomAttributes(true).OfType<IRouteTemplateProvider>().ToArray();
            Assert.That(classRoutes, Has.Length.EqualTo(1), $"{controller.Name} must have exactly one class route");
            string prefix = classRoutes[0].Template!.Replace("[controller]", ControllerToken(controller)).Trim('/');
            bool api = controller.GetCustomAttribute<ApiControllerAttribute>(true) != null;
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<NonActionAttribute>() == null);
            foreach (var action in actions) {
                foreach (var (verbs, template, order) in ActionRoutes(action)) {
                    string full = Combine(prefix, template.Replace("[action]", action.Name));
                    foreach (string verb in verbs) {
                        string line = string.Join(" | ",
                            $"{verb} {full}",
                            $"auth={Authorization(controller, action)}",
                            $"action={action.Name}",
                            $"params={string.Join(", ", action.GetParameters().Select(Parameter))}",
                            $"api={(api ? "yes" : "no")}",
                            $"order={(order?.ToString(CultureInfo.InvariantCulture) ?? "-")}",
                            $"precedence={RoutePrecedence.ComputeInbound(TemplateParser.Parse(full)).ToString(CultureInfo.InvariantCulture)}");
                        yield return new Route(verb, full, line);
                    }
                }
            }
        }
    }

    // Covers the shapes the plugin uses: [HttpX("t")], [HttpX] with [Route("t")], and [HttpX] alone.
    private static IEnumerable<(IEnumerable<string> Verbs, string Template, int? Order)> ActionRoutes(MethodInfo action) {
        var verbAttributes = action.GetCustomAttributes<HttpMethodAttribute>(true).ToArray();
        var routeAttributes = action.GetCustomAttributes<RouteAttribute>(true).ToArray();
        if (verbAttributes.Length == 0 && routeAttributes.Length == 0)
            yield break;
        var templated = verbAttributes.Where(attribute => attribute.Template != null).ToArray();
        Assert.That(templated.Length == 0 || routeAttributes.Length == 0, Is.True,
            $"{action.DeclaringType!.Name}.{action.Name} mixes [Route] with a templated verb attribute, which this table does not model");
        foreach (var attribute in templated)
            yield return (attribute.HttpMethods, attribute.Template!, ((IRouteTemplateProvider)attribute).Order);
        string[] verbless = verbAttributes.Where(attribute => attribute.Template == null).SelectMany(attribute => attribute.HttpMethods).ToArray();
        if (verbless.Length == 0 && templated.Length > 0)
            yield break;
        string[] verbs = verbless.Length == 0 ? ["ANY"] : verbless;
        if (routeAttributes.Length == 0) {
            yield return (verbs, "", null);
            yield break;
        }
        foreach (var attribute in routeAttributes)
            yield return (verbs, attribute.Template, ((IRouteTemplateProvider)attribute).Order);
    }

    private static string Combine(string prefix, string template) {
        if (template.StartsWith("~/", StringComparison.Ordinal))
            return template[2..].Trim('/');
        if (template.StartsWith('/'))
            return template.Trim('/');
        return template.Length == 0 ? prefix : $"{prefix}/{template.Trim('/')}";
    }

    private static string ControllerToken(Type controller) =>
        controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;

    private static string Authorization(Type controller, MethodInfo action) {
        var attributes = action.GetCustomAttributes(true).Concat(controller.GetCustomAttributes(true)).ToArray();
        if (attributes.OfType<IAllowAnonymous>().Any())
            return "anonymous";
        var rules = attributes.OfType<IAuthorizeData>()
            .Select(rule => rule.Policy != null ? $"policy:{rule.Policy}"
                : rule.Roles != null ? $"roles:{rule.Roles}"
                : "authenticated")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        return rules.Length == 0 ? "none" : string.Join(",", rules);
    }

    private static string Parameter(ParameterInfo parameter) {
        var source = parameter.GetCustomAttributes(true).OfType<IBindingSourceMetadata>().FirstOrDefault()?.BindingSource;
        return $"{parameter.Name}{(source == null ? "" : $"[{source.Id}]")}:{TypeName(parameter.ParameterType)}";
    }

    private static string TypeName(Type type) {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return TypeName(underlying) + "?";
        if (type.IsArray)
            return TypeName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.Name;
        string name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}

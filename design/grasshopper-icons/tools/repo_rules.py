"""Repository-specific classification decisions for SAM_SolarCalculator (the only non-shared tool file).

OVERRIDES     : component display name -> (object glyph, op, extra)   extra: None | "plural" | "library" | note
PARAM_OBJECTS : param type key (Goo<X>Param class or typeof(X) name) -> glyph | (glyph, container, plural)
OBJECTS/VERBS : extra noun/verb rules tried before the shared ones (same shapes as SAM's OBJECTS/VERBS)
"""
# Solar exposure/irradiance = ext `solar`; sun direction = ext `sunPath`; shading devices/schemes = SAM `shade`.
OVERRIDES = {
    "SAMAnalytical.AnalysisPeriod": ("clock", "create", "hours of the year analysed"),
    "SAMAnalytical.ApertureIrradiance": ("solar", "calculate", None),
    "SAMAnalytical.ApertureSolarTargets": ("aperture", "filter", "plural"),
    "SAMAnalytical.AssembleShadingSchemes": ("shade", "merge", "plural"),
    "SAMAnalytical.CompareShading": ("shade", "analyse", "plural"),
    "SAMAnalytical.CompareSolarCoverage": ("solar", "analyse", None),
    "SAMAnalytical.IdealShadingShape": ("shade", "create", "ideal shading region"),
    "SAMAnalytical.RationaliseShading": ("shade", "modify", None), "SAMAnalytical.RationaliseAwningGroup": ("shade", "modify", "plural"),
    "SAMAnalytical.SelectShadingScheme": ("shade", "filter", "plural"),
    "SAMAnalytical.ShadingOperation": ("shade", "inspect", "retractable device over a weather year"),
    "SAMAnalytical.ShadingPotentialField": ("solar", "analyse", "plural"),
    "SAMAnalytical.SolarControlProfile": ("profile", "calculate", "hourly solar-control schedule"),
    "SAMAnalytical.SolarSimulation": ("solar", "run", None),
    "SAMAnalytical.SunDirectionByHourOfYear": ("sunPath", "get", None), "SAMAnalytical.SunDirectionByTime": ("sunPath", "get", None),
    "SAMGeometry.SunExposure": ("solar", "get", None),
    "SAMAnalytical.VerifyShading": ("shade", "validate", None),
    "SAMGeometry.ShadingBySunDirection": ("shade", "get", "plural"),
    "SAMGeometry.SunAnalysisBySunDirection": ("solar", "calculate", "plural"),
}
PARAM_OBJECTS = {"ApertureSolarTarget": "aperture", "IdealShadingResult": "shade", "ShadingPotentialField": "solar"}
OBJECTS = []
VERBS = []

/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Input black and white points, a gamma, and output black and white points, on every channel.",
    "CREDIT": "PlayMap (MIT)",
    "CATEGORIES": ["Colour"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "inBlack", "TYPE": "float", "LABEL": "Input black", "DEFAULT": 0.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "inWhite", "TYPE": "float", "LABEL": "Input white", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "gamma", "TYPE": "float", "LABEL": "Gamma", "DEFAULT": 1.0, "MIN": 0.1, "MAX": 4.0 },
        { "NAME": "outBlack", "TYPE": "float", "LABEL": "Output black", "DEFAULT": 0.0, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "outWhite", "TYPE": "float", "LABEL": "Output white", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0 }
    ]
}*/
void main() {
    vec4 c = IMG_THIS_PIXEL(inputImage);
    vec3 v = clamp((c.rgb - vec3(inBlack)) / max(inWhite - inBlack, 0.00001), 0.0, 1.0);
    v = pow(v, vec3(1.0 / max(gamma, 0.01)));
    gl_FragColor = vec4(mix(vec3(outBlack), vec3(outWhite), v), c.a);
}

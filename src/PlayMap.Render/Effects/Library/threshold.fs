/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "White where the picture's luma is above the level, black below; softness widens the edge between them.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Colour"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "level", "TYPE": "float", "LABEL": "Level", "DEFAULT": 0.5, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "softness", "TYPE": "float", "LABEL": "Softness", "DEFAULT": 0.0, "MIN": 0.0, "MAX": 0.5 }
    ]
}*/
void main() {
    vec4 c = IMG_THIS_PIXEL(inputImage);
    float luma = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722));
    float v = softness > 0.0 ? smoothstep(level - softness, level + softness, luma) : step(level, luma);
    gl_FragColor = vec4(vec3(v), c.a);
}

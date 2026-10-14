/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Turns every colour into its opposite; amount mixes from the picture to its negative.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Colour"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "amount", "TYPE": "float", "LABEL": "Amount", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 1.0, "IDENTITY": 0.0 }
    ]
}*/
void main() {
    vec4 c = IMG_THIS_PIXEL(inputImage);
    gl_FragColor = vec4(mix(c.rgb, 1.0 - c.rgb, amount), c.a);
}

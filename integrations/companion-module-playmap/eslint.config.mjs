import { generateEslintConfig } from '@companion-module/tools/eslint/config.mjs'

const config = await generateEslintConfig({ enableTypescript: true })

export default [
	{ ignores: ['dist/', 'pkg/', 'node_modules/', 'src/palette/palette.js', 'eslint.config.mjs', 'build-config.cjs'] },
	...config,
	{
		files: ['test/**/*.ts', 'tools/**/*.ts'],
		rules: {
			'n/no-unpublished-import': 'off',
			'n/no-extraneous-import': 'off',
		},
	},
]

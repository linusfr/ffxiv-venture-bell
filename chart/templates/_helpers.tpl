{{- define "venturebell.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "venturebell.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "venturebell.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{ include "venturebell.selectorLabels" . }}
{{- end -}}

{{- define "venturebell.selectorLabels" -}}
app.kubernetes.io/name: {{ include "venturebell.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/* The secret holding BELL_TOKEN: the one you named, or the one we create. */}}
{{- define "venturebell.secretName" -}}
{{- default (include "venturebell.fullname" .) .Values.existingSecret -}}
{{- end -}}

{{- define "venturebell.pvcName" -}}
{{- default (include "venturebell.fullname" .) .Values.persistence.existingClaim -}}
{{- end -}}

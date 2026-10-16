import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { request, setApiKey } from './api'
import type { Product, Run, Skill } from './types'
import App from './App.vue'
import ArtifactView from './components/ArtifactView.vue'
import FeatureForm from './components/FeatureForm.vue'
import DecisionForm from './components/DecisionForm.vue'
import DeveloperEditor from './components/DeveloperEditor.vue'
import ProductEditor from './components/ProductEditor.vue'
import SkillEditor from './components/SkillEditor.vue'
import RunDetail from './components/RunDetail.vue'
import DeveloperProposalForm from './components/DeveloperProposalForm.vue'

const fetchMock = vi.fn<typeof fetch>()
const wrappers: VueWrapper[] = []
const product: Product = { id: 'portal', name: 'Portal', brief: 'Customer portal', repositoryUrl: 'https://github.com/example/portal.git', repositoryBaseBranch: 'main' }
const analyst: Skill = { roleId: 'business-analyst', domain: 'general', skillId: 'requirements-analysis', name: 'Requirements', prompt: 'Draft requirements.', activeRevision: 1, isEnabled: true }
const specialist: Skill = { roleId: 'developer-backend', domain: 'general', skillId: 'api-review', name: 'Backend', prompt: 'Review APIs.', activeRevision: 2, isEnabled: true }
const id = '11111111-1111-1111-1111-111111111111'
const run: Run = {
  id, productId: product.id, text: 'Export invoices', status: 'awaiting_business_analyst_review', createdAt: '2026-10-16T12:00:00Z',
  businessAnalystId: 'analyst-1', featureSpecificationRevision: 1, architectureRevision: 0, planRevision: 0, reviewerCount: 1,
  failureCode: null, repositoryUrl: product.repositoryUrl, repositoryBaseBranch: 'main', repositoryCommit: 'abc123',
  featureSpecification: { title: 'Export', problemStatement: 'Need exports', userStories: ['As a user, export invoices'], acceptanceCriteria: ['CSV downloads'], constraints: [], outOfScope: [], openQuestions: ['Which format?'] },
  plan: null, codeChange: null,
}
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}
function keep<T extends VueWrapper>(wrapper: T): T { wrappers.push(wrapper); return wrapper }
function sentBody(index = 0): Record<string, unknown> {
  return JSON.parse(String(fetchMock.mock.calls[index]![1]!.body))
}
function mockRun(current: Run = run) {
  fetchMock.mockImplementation(async url => {
    const path = String(url).split('?')[0]
    if (path === `/api/v1/runs/${current.id}`) return json(current)
    return json([])
  })
}
beforeEach(() => { vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); setApiKey('local-development-key') })
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers(); vi.unstubAllGlobals(); setApiKey('') })

describe('API transport', () => {
  it('sends the in-memory key and idempotency header without cookies', async () => {
    fetchMock.mockResolvedValue(json({ ok: true }))
    await request('/products', { method: 'POST', body: { id: 'portal' }, idempotencyKey: 'retry-key' })
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/products', expect.objectContaining({
      method: 'POST', credentials: 'omit', cache: 'no-store',
      headers: { 'X-Api-Key': 'local-development-key', 'Content-Type': 'application/json', 'Idempotency-Key': 'retry-key' },
      body: '{"id":"portal"}',
    }))
  })
  it('shows problem details for rejected requests', async () => {
    fetchMock.mockResolvedValue(json({ title: 'Run changed concurrently' }, 409))
    await expect(request('/runs')).rejects.toThrow('409: Run changed concurrently')
  })
  it('handles empty accepted and no-content responses', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 202 })).mockResolvedValueOnce(new Response(null, { status: 204 }))
    expect(await request('/decision')).toBeUndefined()
    expect(await request('/repository')).toBeUndefined()
  })
  it('reports a network failure rather than pretending the write succeeded', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    await expect(request('/products')).rejects.toThrow('Cannot reach the API')
  })
  it('clears the credential when disconnected', async () => {
    setApiKey('')
    fetchMock.mockResolvedValue(json([]))
    await request('/products')
    expect(fetchMock.mock.calls[0]![1]!.headers).toEqual({ 'X-Api-Key': '' })
  })
  it.each(['proposals-first', 'draft-first'] as const)('submits the %s planning mode', async mode => {
    fetchMock.mockResolvedValue(json({ runId: id }, 202))
    const wrapper = keep(mount(FeatureForm, { props: { products: [product], skills: [analyst] } }))
    await wrapper.find('input[list]').setValue('portal')
    await wrapper.find('textarea').setValue('Export invoices to CSV')
    await wrapper.find('select:has(option[value="draft-first"])').setValue(mode)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(sentBody().planningMode).toBe(mode)
  })
})

describe('developer implementation proposals', () => {
  const developer = { userId: 'alice', displayName: 'Alice', matchedRoles: ['backend'], reviewFocus: 'Data access', proposal: null, noAdditionalInput: false, submittedAt: null }
  it('posts implementation ideas for the exact round, without an approval', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DeveloperProposalForm, { props: { runId: id, revision: 2, developer, disabled: false } }))
    await wrapper.find('textarea').setValue('Use the existing export service and add integration tests.')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(fetchMock.mock.calls[0]![0]).toBe(`/api/v1/runs/${id}/developer-proposals/alice`)
    expect(sentBody()).toEqual({ revision: 2, proposal: 'Use the existing export service and add integration tests.', noAdditionalInput: false })
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })
  it('requires an explicit no-input response instead of silently submitting blank ideas', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DeveloperProposalForm, { props: { runId: id, revision: 1, developer, disabled: false } }))
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').text()).toContain('explicitly choose')
    await wrapper.find('input[type="checkbox"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(sentBody()).toEqual({ revision: 1, proposal: null, noAdditionalInput: true })
  })
  it('prefills prior ideas but requires a fresh confirmation and retains network failures', async () => {
    fetchMock.mockRejectedValue(new TypeError('Offline'))
    const wrapper = keep(mount(DeveloperProposalForm, { props: { runId: id, revision: 3, developer: { ...developer, proposal: 'Reuse service' }, disabled: false } }))
    expect(wrapper.text()).toContain('has not been submitted for this revision')
    expect(wrapper.find('textarea').element.value).toBe('Reuse service')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Cannot reach the API')
    expect(wrapper.emitted('saved')).toBeUndefined()
  })
})

describe('feature intake', () => {
  async function form() {
    const wrapper = keep(mount(FeatureForm, { props: { products: [product], skills: [analyst, specialist] } }))
    await wrapper.find('input[list]').setValue('portal')
    await wrapper.find('textarea').setValue('Export invoices to CSV')
    return wrapper
  }
  it.each(['defaults', 'selected', 'none'] as const)('preserves %s specialist selection semantics', async mode => {
    fetchMock.mockResolvedValue(json({ runId: id, status: 'queued' }, 202))
    const wrapper = await form()
    await wrapper.find('select:has(option[value="none"])').setValue(mode)
    if (mode === 'selected') await wrapper.find('input[type="checkbox"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const body = sentBody()
    if (mode === 'defaults') expect(body).not.toHaveProperty('developerSkills')
    if (mode === 'none') expect(body.developerSkills).toEqual([])
    if (mode === 'selected') expect(body.developerSkills).toEqual([{ roleId: specialist.roleId, domain: specialist.domain, skillId: specialist.skillId }])
    expect(body.reviewerCount).toBe(3)
    expect(wrapper.emitted('submitted')?.[0]).toEqual([id])
  })
  it('reuses a submission key after a lost response and rotates it for changed intent', async () => {
    fetchMock.mockRejectedValue(new TypeError('Network failure'))
    const wrapper = await form()
    await wrapper.find('form').trigger('submit'); await flushPromises()
    await wrapper.find('form').trigger('submit'); await flushPromises()
    const header = (index: number) => (fetchMock.mock.calls[index]![1]!.headers as Record<string, string>)['Idempotency-Key']
    expect(header(0)).toBe(header(1))
    await wrapper.find('textarea').setValue('A different feature')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(header(2)).not.toBe(header(0))
    expect(wrapper.find('[role="alert"]').text()).toContain('Cannot reach the API')
  })
  it('rotates the retry identity when the developer planning approach changes', async () => {
    fetchMock.mockRejectedValue(new TypeError('Network failure'))
    const wrapper = await form()
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const first = fetchMock.mock.calls[0]![1]!.headers
    await wrapper.find('select:has(option[value="draft-first"])').setValue('draft-first')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(fetchMock.mock.calls[1]![1]!.headers).not.toEqual(first)
    expect(sentBody(1).planningMode).toBe('draft-first')
  })
  it('rejects an empty explicit selection', async () => {
    const wrapper = await form()
    await wrapper.find('select:has(option[value="none"])').setValue('selected')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Select at least one specialist')
  })
})

describe('configuration forms', () => {
  it('uses identifier patterns compatible with modern browser Unicode-set validation', () => {
    const components = [
      keep(mount(ProductEditor, { props: { products: [] } })),
      keep(mount(FeatureForm, { props: { products: [], skills: [analyst] } })),
      keep(mount(SkillEditor, { props: { skills: [] } })),
    ]
    for (const wrapper of components) {
      for (const input of wrapper.findAll('input[pattern]')) {
        const pattern = new RegExp(`^(?:${input.attributes('pattern')})$`, 'v')
        expect(pattern.test('demo-product')).toBe(true)
        expect(pattern.test('invalid/id')).toBe(false)
        expect(pattern.test('Uppercase')).toBe(false)
      }
    }
  })
  it('registers product context and normalizes an empty base branch', async () => {
    fetchMock.mockResolvedValue(json(product, 201))
    const wrapper = keep(mount(ProductEditor, { props: { products: [] } }))
    const inputs = wrapper.findAll('input')
    await inputs[0]!.setValue('portal'); await inputs[1]!.setValue('Portal')
    await inputs[2]!.setValue(product.repositoryUrl)
    await wrapper.find('textarea').setValue(product.brief)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(sentBody()).toEqual({ ...product, repositoryBaseBranch: null })
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })
  it('updates only repository settings for an existing product', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }))
    const wrapper = keep(mount(ProductEditor, { props: { products: [product] } }))
    await wrapper.find('.list-item').trigger('click')
    expect(wrapper.find('input').attributes('disabled')).toBeDefined()
    await wrapper.findAll('input')[3]!.setValue('develop')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock.mock.calls[0]![0]).toBe('/api/v1/products/portal/repository')
    expect(sentBody()).toEqual({ repositoryUrl: product.repositoryUrl, repositoryBaseBranch: 'develop' })
  })
  it('saves developer roles as an array and encodes the developer ID', async () => {
    fetchMock.mockResolvedValue(json({}))
    const wrapper = keep(mount(DeveloperEditor, { props: { developers: [] } }))
    await wrapper.findAll('input')[0]!.setValue('team/alice')
    await wrapper.findAll('input')[1]!.setValue('Alice')
    await wrapper.find('textarea').setValue('backend\n database ')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock.mock.calls[0]![0]).toBe('/api/v1/developers/team%2Falice')
    expect(sentBody().roles).toEqual(['backend', 'database'])
  })
  it('rejects duplicate reviewer roles', async () => {
    const wrapper = keep(mount(DeveloperEditor, { props: { developers: [] } }))
    await wrapper.find('textarea').setValue('Backend\nbackend')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('1-10 unique roles')
  })
  it('creates immutable prompt revisions and loads revision history', async () => {
    fetchMock.mockImplementation(async () => json([]))
    const wrapper = keep(mount(SkillEditor, { props: { skills: [specialist] } }))
    await wrapper.find('.list-item').trigger('click'); await flushPromises()
    await wrapper.find('textarea').setValue('New specialist instructions')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock.mock.calls[1]![0]).toBe('/api/v1/agent-skills/developer-backend/general/api-review/revisions')
    expect(sentBody(1)).toMatchObject({ prompt: 'New specialist instructions', isEnabled: true })
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })
})

describe('human review decisions', () => {
  it.each([
    ['business-analyst', 'Feature specification', 'Approve specification', 'architecture analysis'],
    ['architecture', 'Architecture analysis', 'Approve architecture', 'specialist analysis'],
    ['assignment', 'Implementation plan', 'Approve plan as reviewer', 'separate final plan decision'],
    ['plan', 'Implementation plan', 'Approve plan and start implementation', 'does not merge or deploy'],
  ] as const)('names the document, revision and consequence for %s', (stage, title, action, consequence) => {
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 3, stage, actorId: 'alice', reviewerName: 'Alice', documentId: 'review-document' } }))
    expect(wrapper.find('h3').text()).toContain(`${title} / revision 3`)
    expect(wrapper.find('a').attributes('href')).toBe('#review-document')
    expect(wrapper.find('button').text()).toBe(`${action} (r3)`)
    expect(wrapper.text()).toContain(consequence)
    expect(wrapper.findAll('label').at(-1)!.text()).toContain(`${title.toLowerCase()} revision 3`)
    if (stage === 'assignment') expect(wrapper.text()).toContain('Responding as Alice')
  })
  it('explains that reviewer feedback is not a final revision decision', async () => {
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 2, stage: 'assignment', actorId: 'alice' } }))
    await wrapper.find('select').setValue('revise')
    expect(wrapper.find('button').text()).toBe('Request plan changes as reviewer (r2)')
    expect(wrapper.text()).toContain('does not start a new revision by itself')
  })
  it.each(['business-analyst', 'architecture', 'assignment'] as const)('enables %s approval unless explicitly gated', stage => {
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage } }))
    expect(wrapper.find('option[value="approve"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('button').attributes('disabled')).toBeUndefined()
  })
  it.each(['business-analyst', 'architecture', 'plan'] as const)('posts a revision-bound %s decision', async stage => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 2, stage, actorId: 'alice' } }))
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock.mock.calls[0]![0]).toBe(`/api/v1/runs/${id}/${stage}-decisions`)
    expect(sentBody()).toMatchObject({ revision: 2, decision: 'approve', actorId: 'alice' })
  })
  it('requires BA answers for revision and permits answers without extra feedback', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage: 'business-analyst', questions: ['Which format?'] } }))
    await wrapper.find('select').setValue('revise')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    await wrapper.find('textarea').setValue('CSV')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(sentBody().answers).toEqual([{ question: 'Which format?', answer: 'CSV' }])
    expect(sentBody().comment).toBeNull()
  })
  it('allows BA approval of the current specification without imposing a new answer policy', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage: 'business-analyst', questions: ['Which format?'] } }))
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(sentBody()).toMatchObject({ decision: 'approve', answers: [] })
  })
  it('records a developer response without inventing a final plan approval', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }))
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage: 'assignment', actorId: 'alice' } }))
    await wrapper.find('select').setValue('revise')
    await wrapper.find('textarea').setValue('Add an integration test')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock.mock.calls[0]![0]).toBe(`/api/v1/runs/${id}/assignments/alice/response`)
    expect(sentBody()).toEqual({ decision: 'revise', response: 'Add an integration test' })
  })
  it('disables plan approval when not all developers approve', () => {
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage: 'plan', canApprove: false } }))
    expect(wrapper.find('button').attributes('disabled')).toBeDefined()
    expect(wrapper.find('option[value="approve"]').attributes('disabled')).toBeDefined()
  })
  it('requires feedback for rejection', async () => {
    const wrapper = keep(mount(DecisionForm, { props: { runId: id, revision: 1, stage: 'plan' } }))
    await wrapper.find('select').setValue('reject')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Provide feedback')
  })
})

describe('workflow monitoring', () => {
  it('collects all developers ideas instead of showing plan approval during proposal collection', async () => {
    mockRun({ ...run, status: 'awaiting_developer_proposals', planningMode: 'draft-first', planRevision: 1, plan: { summary: 'Initial draft', steps: ['Draft step'], openQuestions: [] } })
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, options) => String(url).endsWith('/developer-proposals')
      ? json([{ revision: 2, status: 'collecting', developers: [{ userId: 'alice', displayName: 'Alice', matchedRoles: ['backend'], reviewFocus: null, proposal: 'Use CSV library', noAdditionalInput: false, submittedAt: null }] }])
      : original(url, options))
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    expect(wrapper.findComponent(DeveloperProposalForm).props('revision')).toBe(2)
    expect(wrapper.findAllComponents(DecisionForm)).toHaveLength(0)
    expect(wrapper.find('#current-plan').text()).toContain('Initial AI draft')
    expect(wrapper.text()).toContain('0 / 1 inputs confirmed')
  })
  it('keeps only the exact current architecture revision next to its decision', async () => {
    mockRun({ ...run, status: 'awaiting_architecture_review', architectureRevision: 2 })
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, options) => String(url).endsWith('/architecture')
      ? json([{ revision: 3, analysis: { summary: 'Not the requested revision' } }, { revision: 2, analysis: { summary: 'Current technical proposal' } }, { revision: 1, analysis: { summary: 'Old technical proposal' } }])
      : original(url, options))
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    const section = wrapper.find('#current-architecture')
    expect(section.text()).toContain('Current technical proposal')
    expect(section.text()).not.toContain('Old technical proposal')
    expect(section.text()).not.toContain('Not the requested revision')
    expect(section.findComponent(DecisionForm).props()).toMatchObject({ revision: 2, stage: 'architecture', documentId: 'current-architecture', disabled: false })
    expect(wrapper.find('.review-task').text()).toContain('not its history')
    expect(wrapper.findAll('details').filter(item => item.text().includes('history & evidence')).every(item => item.attributes('open') === undefined)).toBe(true)
  })
  it('blocks architecture decisions if the matching document is missing', async () => {
    mockRun({ ...run, status: 'awaiting_architecture_review', architectureRevision: 2 })
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    expect(wrapper.find('#current-architecture [role="alert"]').text()).toContain('revision 2 is unavailable')
    expect(wrapper.findComponent(DecisionForm).props('disabled')).toBe(true)
  })
  it('distinguishes reviewer responses from the separate final plan authorization', async () => {
    mockRun({ ...run, status: 'awaiting_approval', planRevision: 2, plan: { summary: 'Build CSV download', steps: ['Add endpoint'], openQuestions: [] } })
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, options) => String(url).endsWith('/assignments')
      ? json([{ revision: 2, userId: 'alice', developerDisplayName: 'Alice', matchedRoles: ['frontend'], reviewFocus: 'Browser behavior', reviewPrompt: 'Review carefully', decision: null, response: null, status: 'pending' }])
      : original(url, options))
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    const forms = wrapper.findAllComponents(DecisionForm)
    expect(forms).toHaveLength(2)
    expect(forms[0]!.props()).toMatchObject({ stage: 'assignment', documentId: 'current-plan', revision: 2, reviewerName: 'Alice' })
    expect(forms[1]!.props()).toMatchObject({ stage: 'plan', documentId: 'current-plan', revision: 2, disabled: true })
    expect(wrapper.find('#current-plan').text()).toContain('Build CSV download')
    expect(wrapper.find('.review-task').text()).toContain('separate final decision')
  })
  it('loads evidence, enables the matching review gate and renders untrusted output as text', async () => {
    mockRun()
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    expect(wrapper.text()).toContain('Business analyst specification / r1')
    expect(wrapper.findAllComponents(DecisionForm)).toHaveLength(1)
    const artifact = keep(mount(ArtifactView, { props: { value: { summary: '<img src=x onerror=alert(1)>' } } }))
    expect(artifact.find('img').exists()).toBe(false)
    expect(artifact.text()).toContain('<img src=x')
  })
  it('reads subsequent event pages', async () => {
    mockRun()
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, options) => {
      if (String(url).endsWith('/events?after=0')) return json(Array.from({ length: 100 }, (_, index) => ({ id: index + 1, kind: 'event', actor: 'workflow', createdAt: run.createdAt })))
      if (String(url).endsWith('/events?after=100')) return json([{ id: 101, kind: 'last_event', actor: 'workflow', createdAt: run.createdAt }])
      return original(url, options)
    })
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    expect(wrapper.findAll('.timeline li')).toHaveLength(101)
  })
  it.each(['code_change_ready', 'feature_request_rejected', 'architecture_rejected', 'plan_rejected', 'failed', 'blocked'])('stops polling at %s', async status => {
    vi.useFakeTimers()
    mockRun({ ...run, status })
    keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    const count = fetchMock.mock.calls.length
    await vi.advanceTimersByTimeAsync(6000)
    expect(fetchMock.mock.calls.length).toBe(count)
  })
  it('surfaces polling failures and disables stale review actions', async () => {
    mockRun()
    const wrapper = keep(mount(RunDetail, { props: { runId: id } }))
    await flushPromises()
    fetchMock.mockImplementation(async () => json({ title: 'Unavailable' }, 503))
    await wrapper.find('.section-heading button').trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('503: Unavailable')
    expect(wrapper.findComponent(DecisionForm).props('disabled')).toBe(true)
  })
})

describe('workspace connection', () => {
  it('loads API directories, navigates all sections and disconnects without browser storage', async () => {
    fetchMock.mockImplementation(async url => {
      if (String(url).includes('/products?')) return json({ items: [product], total: 1 })
      if (String(url).includes('/runs?')) return json({ items: [], total: 0 })
      if (String(url).endsWith('/agent-skills')) return json([analyst, specialist])
      return json([])
    })
    const wrapper = keep(mount(App))
    await wrapper.find('input[type="password"]').setValue('a'.repeat(32))
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(wrapper.text()).toContain('API connected')
    for (const button of wrapper.findAll('nav button')) { await button.trigger('click'); expect(wrapper.find('h1').text()).toBe(button.text().slice(2)) }
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
    await wrapper.find('.topbar button').trigger('click')
    expect(wrapper.find('input[type="password"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('API connected')
  })
})
